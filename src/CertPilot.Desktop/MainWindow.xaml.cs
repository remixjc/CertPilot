using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CertPilot.Desktop;

public partial class MainWindow : Window
{
    private PilotClient? client;
    private UserInfo? user;
    private SystemInfo system = new();
    private List<CertificateItem> certificates = [];
    private List<CredentialItem> credentials = [];
    private bool busy;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private CertificateItem? Selected => CertificateGrid.SelectedItem as CertificateItem;
    public MainWindow()
    {
        InitializeComponent();
        var settings = DesktopSettings.Load(); EndpointBox.Text = settings.Endpoint; DataDirectoryBox.Text = settings.DataDirectory;
        timer.Tick += async (_, _) => { if (!busy && user is not null && IsActive) await Run(Refresh); };
        timer.Start();
        Closed += (_, _) => { timer.Stop(); client?.Dispose(); };
        UpdateLocalStatus();
    }
    private async Task Run(Func<Task> action, string? success = null)
    {
        if (busy) return;
        busy = true; BusyOverlay.Visibility = Visibility.Visible;
        try
        {
            await action();
            if (success is not null) Tell(success);
        }
        catch (Exception e)
        {
            if (e is PilotClientException { Status: HttpStatusCode.Unauthorized }) ShowLogin();
            Tell(e is HttpRequestException ? "无法连接服务。请检查服务状态、地址和 HTTPS 管理证书。" : e.Message, true);
        }
        finally { busy = false; BusyOverlay.Visibility = Visibility.Collapsed; UpdateLocalStatus(); }
    }
    private void Tell(string message, bool error = false)
    {
        Notice.Visibility = Visibility.Visible; NoticeText.Text = message;
        Notice.Background = new System.Windows.Media.SolidColorBrush(error ? System.Windows.Media.Color.FromRgb(252, 237, 230) : System.Windows.Media.Color.FromRgb(233, 242, 233));
    }
    private async Task Connect()
    {
        client?.Dispose(); client = new PilotClient(EndpointBox.Text, DataDirectoryBox.Text);
        await client.RefreshCsrf();
        await client.Write<JsonElement>("auth/login", HttpMethod.Post, new { username = UsernameBox.Text, password = LoginPassword.Password });
        await client.RefreshCsrf(); user = await client.Get<UserInfo>("auth/me");
        if (user.MustChangePassword)
        {
            var passwordDialog = new FormDialog(this, "首次登录 · 修改临时密码");
            passwordDialog.Height = 480;
            passwordDialog.Note("至少 14 位，包含大写、小写、数字和符号。修改成功后自动删除 bootstrap-admin.txt。");
            var password = passwordDialog.Password("新密码");
            var confirm = passwordDialog.Password("确认新密码");
            passwordDialog.Submit = async () =>
            {
                if (password.Password != confirm.Password) throw new InvalidOperationException("两次输入的密码不一致。");
                await client.Write<object>("auth/password", HttpMethod.Post, new { currentPassword = LoginPassword.Password, newPassword = password.Password });
                password.Clear(); confirm.Clear();
            };
            // The modal handles its own async save while the main window is temporarily idle.
            BusyOverlay.Visibility = Visibility.Collapsed;
            if (passwordDialog.ShowDialog() != true) { await client.Write<object>("auth/logout", HttpMethod.Post, new { }); ShowLogin(); return; }
            BusyOverlay.Visibility = Visibility.Visible;
            await client.RefreshCsrf(); user = await client.Get<UserInfo>("auth/me");
        }
        LoginPassword.Clear();
        new DesktopSettings { Endpoint = EndpointBox.Text, DataDirectory = DataDirectoryBox.Text }.Save();
        ShowWorkspace(); await Refresh(); Tell("已连接后台服务。关闭此窗口不会停止自动续期。");
    }
    private void ShowWorkspace()
    {
        LoginPanel.Visibility = Visibility.Collapsed; Workspace.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = true; LogoutButton.Visibility = Visibility.Visible;
        AccountLabel.Text = user!.UserName + " · " + string.Join(" / ", user.Roles);
        CredentialsTab.Visibility = user.CanWrite ? Visibility.Visible : Visibility.Collapsed;
        NewCertificateButton.IsEnabled = user.CanWrite;
        ConnectionLabel.Text = client!.Endpoint.ToString();
    }
    private void ShowLogin()
    {
        user = null; LoginPassword.Clear();
        LoginPanel.Visibility = Visibility.Visible; Workspace.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = false; LogoutButton.Visibility = Visibility.Collapsed; AccountLabel.Text = "本机证书工作台";
        CertificateGrid.ItemsSource = null; CredentialGrid.ItemsSource = null; JobGrid.ItemsSource = null;
        SystemDetails.Clear(); JobDetails.Clear(); certificates.Clear(); credentials.Clear();
        client?.Dispose(); client = null;
    }
    private async Task Refresh()
    {
        if (client is null || user is null) return;
        var selectedId = Selected?.Id;
        certificates = await client.All<CertificateItem>("certificates");
        system = await client.Get<SystemInfo>("system/status");
        var jobs = (await client.Get<Page<JobItem>>("jobs?limit=100")).Items;
        if (user.CanWrite) credentials = (await client.Get<Page<CredentialItem>>("credentials")).Items;
        CertificateGrid.ItemsSource = certificates; CertificateGrid.SelectedItem = certificates.FirstOrDefault(c => c.Id == selectedId);
        CredentialGrid.ItemsSource = credentials; JobGrid.ItemsSource = jobs;
        EmptyCertificates.Visibility = certificates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsLabel.Text = $"托管 {certificates.Count} 张    ·    有效 {certificates.Count(c => c.Certificate.Status == "valid")} 张    ·    自动续期 {certificates.Count(c => c.AutoRenew)} 项    ·    需关注 {certificates.Count(c => c.Renewal.Error is not null)} 项";
        SystemDetails.Text = $"Worker / Web：{system.Service.GetProperty("worker").GetProperty("status").GetString()} / {system.Service.GetProperty("web").GetProperty("status").GetString()}\n" +
            $"运行中 {system.Running} 项 / 排队 {system.Queued} 项\n\n允许输出根目录：\n{string.Join(Environment.NewLine, system.OutputRoots)}\n\n允许 HTTP Webroot：\n{string.Join(Environment.NewLine, system.WebRoots)}";
        StatusLabel.Text = "已更新 " + DateTime.Now.ToString("HH:mm:ss") + "   ·   关闭管理端不会停止 Windows 服务";
        UpdateActions();
    }
    private void UpdateActions()
    {
        CertificateActions.IsEnabled = Selected is not null;
        foreach (var button in new[] { EditCertificateButton, IssueButton, RenewButton, ToggleButton, InspectButton, RemoveButton }) button.IsEnabled = user?.CanWrite == true;
        if (Selected is { } selected) { RenewButton.IsEnabled &= selected.Certificate.NotAfterUtc is not null; ToggleButton.Content = selected.AutoRenew ? "暂停自动续期" : "恢复自动续期"; }
    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await Run(Connect);
    private async void Password_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Run(Connect); } }
    private async void Logout_Click(object sender, RoutedEventArgs e) => await Run(async () => { if (client is not null) await client.Write<object>("auth/logout", HttpMethod.Post, new { }); ShowLogin(); });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Run(Refresh);
    private void Certificate_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void Certificate_DoubleClick(object sender, MouseButtonEventArgs e) { if (Selected is not null) Details_Click(sender, e); }
    private async void NewCertificate_Click(object sender, RoutedEventArgs e) { if (client is null) return; if (DesktopDialogs.Certificate(this, client, system, credentials, null).ShowDialog() == true) await Run(Refresh, "配置已保存，请选择证书后发起申请。"); }
    private async void EditCertificate_Click(object sender, RoutedEventArgs e) { if (client is null || Selected is null) return; if (DesktopDialogs.Certificate(this, client, system, credentials, Selected).ShowDialog() == true) await Run(Refresh, "配置已保存。"); }
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        DesktopDialogs.Details(this, c.Name + " · 实际证书与配置", $"配置域名：{string.Join(", ", c.Domains)}\n实际证书域名：{string.Join(", ", c.Certificate.Domains)}\n\n有效期至：{c.ExpiryText}\n指纹：{c.Certificate.Thumbprint ?? "尚未签发"}\n\n配置版本：{c.Revision} / 已签发版本：{c.Certificate.IssuedRevision}\n\n输出目录：{c.OutputDirectory}\n当前实际版本目录：{c.Certificate.CurrentGeneration ?? "—"}\n\n下次续期：{c.Renewal.NextAttemptAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}\n最近错误：{c.Renewal.Error ?? "无"}\n\n证书输出成功不代表 IIS / Nginx 已加载，需单独部署并执行网站检测。");
    }
    private async Task Enqueue(string kind)
    {
        if (client is null || Selected is not { } c) return;
        if (MessageBox.Show(this, $"确认对“{c.Name}”发起{(kind == "issuances" ? "证书申请" : "续期")}？\nCA 环境：{c.EnvironmentText}。任务会在后台继续执行。", "提交签发任务", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async () => { await client.Write<JsonElement>($"certificates/{c.Id}/{kind}", HttpMethod.Post, new { }); await Refresh(); }, "任务已持久化排队，请在任务记录中查看阶段与结果。");
    }
    private async void Issue_Click(object sender, RoutedEventArgs e) => await Enqueue("issuances");
    private async void Renew_Click(object sender, RoutedEventArgs e) => await Enqueue("renewals");
    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || Selected is not { } c) return;
        await Run(async () => { await client.Write<CertificateItem>("certificates/" + c.Id, HttpMethod.Put, c.Input(!c.AutoRenew), c.Revision); await Refresh(); }, c.AutoRenew ? "已暂停自动续期。" : "已恢复自动续期。");
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || Selected is not { } c) return;
        if (MessageBox.Show(this, $"停止托管“{c.Name}”？自动续期将停止，已有证书文件保留。", "停止托管", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(async () => { await client.Write<object>("certificates/" + c.Id, HttpMethod.Delete, revision: c.Revision); await Refresh(); }, "已停止托管，现有文件保留。");
    }
    private void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || Selected is not { } c) return;
        var domains = c.Domains.Where(d => !d.StartsWith("*.")).ToArray();
        if (domains.Length == 0) { Tell("通配符不能直接检测，请先在配置中添加具体域名。", true); return; }
        var dialog = new FormDialog(this, "检测网站实际 TLS 证书"); dialog.Height = 360;
        dialog.Note("由服务器连接所选公网域名的 443 端口，校验信任链、域名与托管证书指纹。不会部署证书。");
        var domain = dialog.Choice("具体域名", domains.Select(d => (d, d)), domains[0]);
        dialog.Submit = async () => { await client.Write<JsonElement>($"certificates/{c.Id}/inspections", HttpMethod.Post, new { domain = domain.SelectedValue }); };
        if (dialog.ShowDialog() == true) Tell("网站检测任务已排队，请在任务记录中查看结果。");
    }
    private async void NewCredential_Click(object sender, RoutedEventArgs e) { if (client is not null && DesktopDialogs.Credential(this, client, null).ShowDialog() == true) await Run(Refresh, "DNS 凭据已加密保存。"); }
    private async void UpdateCredential_Click(object sender, RoutedEventArgs e) { if (client is not null && CredentialGrid.SelectedItem is CredentialItem c && DesktopDialogs.Credential(this, client, c).ShowDialog() == true) await Run(Refresh, "DNS Token 已替换。"); }
    private void Job_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (JobGrid.SelectedItem is not JobItem j) return;
        JobDetails.Text = $"任务 {j.Id}\n{j.StatusText} / {j.Stage}\n{j.Error}\n" + (j.Result is null ? "" : JsonSerializer.Serialize(j.Result, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void Section_Changed(object sender, SelectionChangedEventArgs e) { if (e.Source == Sections) UpdateLocalStatus(); }
    private void UpdateLocalStatus()
    {
        if (LocalServiceLabel is null) return;
        LocalServiceLabel.Text = "当前电脑：" + (LocalService.Status() switch { "NotInstalled" => "尚未安装服务", "Running" => "服务正在运行", "Stopped" => "服务已停止", var other => other });
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将在本机 Program Files 安装 / 更新 CertPilot，并注册自动启动的 Windows 服务。更新期间服务会短暂停止，配置与证书保留。", "安装 / 更新本机服务", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await Run(LocalService.Install, "本机服务已安装 / 更新并启动。首次使用请读取初始账号，然后登录。");
    }
    private async void StartService_Click(object sender, RoutedEventArgs e) => await Run(LocalService.Start, "已启动本机服务。");
    private async void StopService_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "停止服务将同时停止自动续期和 Web API，确认继续？", "停止本机服务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Run(LocalService.Stop, "本机服务已停止。");
    }
    private void LocalService_Click(object sender, RoutedEventArgs e) => UpdateLocalStatus();
    private void Bootstrap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(DataDirectoryBox.Text, "bootstrap-admin.txt"));
            UsernameBox.Text = lines.First(l => l.StartsWith("Username: "))[10..]; LoginPassword.Password = lines.First(l => l.StartsWith("Password: "))[10..];
            Tell("已读取本机初始账号。登录后必须修改临时密码。");
        }
        catch (Exception exception) { Tell(exception is FileNotFoundException ? "未找到初始账号文件。请先安装并启动服务；修改初始密码后此文件会自动删除。" : exception.Message, true); }
    }
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    private void OpenWeb_Click(object sender, RoutedEventArgs e) { if (client is not null) Open(client.Endpoint.ToString()); }
    private void OpenApi_Click(object sender, RoutedEventArgs e) { if (client is not null) Open(new Uri(client.Endpoint, "api-docs.html").ToString()); }
    private void OpenData_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(DataDirectoryBox.Text)) Open(DataDirectoryBox.Text); else Tell("本机数据目录不存在，请先安装服务。", true); }
    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CertPilot", "service", "appsettings.Local.json");
        if (!File.Exists(config)) { Tell("尚未找到已安装的服务配置，请先安装服务。", true); return; }
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(config); Process.Start(start);
        Tell("修改本机配置后需要重启服务。远程地址的配置需在对应服务器上维护。");
    }
    public async Task SmokeTestAsync()
    {
        if (!IsLoaded || LoginPanel.Visibility != Visibility.Visible || LoginPassword is null) throw new InvalidOperationException("Native login view did not load.");
        using var testClient = new PilotClient("https://localhost:8443", Path.GetTempPath());
        var nativeEditor = DesktopDialogs.Certificate(this, testClient, new SystemInfo(), [], null);
        nativeEditor.Show(); App.SaveScreenshot(nativeEditor, "desktop-certificate-editor.png"); nativeEditor.Close();
        var nativeCredential = DesktopDialogs.Credential(this, testClient, null);
        nativeCredential.Show(); App.SaveScreenshot(nativeCredential, "desktop-credential-editor.png"); nativeCredential.Close();
        var endpoint = Environment.GetEnvironmentVariable("CERTPILOT_SMOKE_ENDPOINT");
        var directory = Environment.GetEnvironmentVariable("CERTPILOT_SMOKE_DATA");
        if (endpoint is null || directory is null) return;
        using var connected = new PilotClient(endpoint, directory);
        var password = File.ReadLines(Path.Combine(directory, "bootstrap-admin.txt")).First(l => l.StartsWith("Password: "))[10..];
        await connected.RefreshCsrf();
        await connected.Write<JsonElement>("auth/login", HttpMethod.Post, new { username = "admin", password });
        await connected.RefreshCsrf();
        await connected.Write<object>("auth/password", HttpMethod.Post, new { currentPassword = password, newPassword = "Smoke!" + Guid.NewGuid().ToString("N") });
        if (File.Exists(Path.Combine(directory, "bootstrap-admin.txt"))) throw new InvalidOperationException("Bootstrap password was not removed.");
        await connected.RefreshCsrf();
        var info = await connected.Get<SystemInfo>("system/status");
        var c = await connected.Write<CredentialItem>("credentials", HttpMethod.Post, new { name = "Smoke DNS", zoneId = new string('a', 32), zoneName = "example.com", token = "fake-smoke-token-never-used-for-issuance" });
        var created = await connected.Write<CertificateItem>("certificates", HttpMethod.Post, new CertificateInput("Smoke Certificate", ["example.com"], "smoke@example.com", "staging", "dns", c.Id, null, info.OutputRoots[0], "smoke", "both", false, false));
        client = connected; user = await connected.Get<UserInfo>("auth/me");
        ShowWorkspace(); await Refresh(); App.SaveScreenshot(this, "desktop-certificates.png");
        var updated = await connected.Write<CertificateItem>("certificates/" + created.Id, HttpMethod.Put, created.Input(true), created.Revision);
        if (updated.Revision != created.Revision + 1 || !updated.AutoRenew) throw new InvalidOperationException("Revision update failed.");
        try { await connected.Write<CertificateItem>("certificates/" + created.Id, HttpMethod.Put, created.Input(), created.Revision); throw new InvalidOperationException("Stale revision was accepted."); }
        catch (PilotClientException exception) when (exception.Status == HttpStatusCode.PreconditionFailed) { }
        await connected.Write<object>("certificates/" + created.Id, HttpMethod.Delete, revision: updated.Revision);
        if ((await connected.All<CertificateItem>("certificates")).Count != 0) throw new InvalidOperationException("Removed certificate remains.");
        if (LocalService.Status() != "Running") throw new InvalidOperationException("Windows service is not running.");
    }
}
