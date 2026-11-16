using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CertPilot.Desktop;

public sealed class FormDialog : Window
{
    private readonly StackPanel fields = new();
    private readonly TextBlock error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button save;
    public Func<Task>? Submit { get; set; }
    public FormDialog(Window owner, string title, int width = 600)
    {
        Owner = owner; Title = title; Width = width; Height = 680; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var layout = new DockPanel { Margin = new Thickness(28) };
        var heading = new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Click += (_, _) => Close();
        save = new Button { Content = "保存", Style = (Style)FindResource("Primary"), IsDefault = true };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false; error.Text = "";
            try { if (Submit is not null) await Submit(); DialogResult = true; }
            catch (Exception e) { error.Text = e.Message; }
            finally { save.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        fields.Children.Add(error); layout.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
    }
    public TextBox Text(string label, string value = "", bool multiline = false, bool readOnly = false)
    {
        Label(label);
        var field = new TextBox { Text = value, IsReadOnly = readOnly, Margin = new Thickness(0, 0, 0, 16) };
        if (multiline) { field.AcceptsReturn = true; field.Height = 90; field.TextWrapping = TextWrapping.Wrap; field.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        fields.Children.Add(field); return field;
    }
    public TextBox Folder(string label, string value, bool local, bool readOnly = false)
    {
        if (!local || readOnly) return Text(label, value, readOnly: readOnly);
        Label(label);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var field = new TextBox { Text = value };
        var browse = new Button { Content = "浏览…", Margin = new Thickness(10, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = label, Multiselect = false };
            if (dialog.ShowDialog(this) == true) field.Text = dialog.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(field); fields.Children.Add(row); return field;
    }
    public PasswordBox Password(string label)
    {
        Label(label); var field = new PasswordBox { Margin = new Thickness(0, 0, 0, 16) };
        fields.Children.Add(field); return field;
    }
    public ComboBox Choice(string label, IEnumerable<(string Id, string Name)> values, string? selected)
    {
        Label(label);
        var field = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key", Margin = new Thickness(0, 0, 0, 16), ItemsSource = values.Select(v => new KeyValuePair<string, string>(v.Id, v.Name)).ToList(), SelectedValue = selected };
        fields.Children.Add(field); return field;
    }
    public CheckBox Check(string label, bool value)
    {
        var field = new CheckBox { Content = label, IsChecked = value }; fields.Children.Add(field); return field;
    }
    public void Note(string note) => fields.Children.Add(new TextBlock { Text = note, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), FontSize = 11, Margin = new Thickness(0, 0, 0, 16) });
    public void Control(UIElement control) => fields.Children.Add(control);
    public void Label(string label) => fields.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
}

public static class DesktopDialogs
{
    public static FormDialog Certificate(Window owner, PilotClient client, SystemInfo system, List<CredentialItem> credentials, CertificateItem? existing)
    {
        var dialog = new FormDialog(owner, existing is null ? "新建证书配置" : "编辑证书配置", 680);
        var name = dialog.Text("证书名称 *", existing?.Name ?? "");
        var domains = dialog.Text("域名 *（每行一个）", string.Join(Environment.NewLine, existing?.Domains ?? []), multiline: true);
        var email = dialog.Text("联系邮箱 *", existing?.Email ?? "");
        var environment = dialog.Choice("CA 环境", [("staging", "Staging · 测试证书，不受浏览器信任"), ("production", "Production · 正式证书")], existing?.Environment ?? "staging");
        var validation = dialog.Choice("验证方式", [("dns", "DNS-01 · Cloudflare，支持通配符"), ("http", "HTTP-01 · 网站 Webroot")], existing?.Validation ?? "dns");
        var credential = dialog.Choice("DNS 凭据（DNS-01 必填）", credentials.Select(c => (c.Id, c.Name + " · " + c.ZoneName)), existing?.CredentialId ?? credentials.FirstOrDefault()?.Id);
        var webroot = dialog.Folder("网站 Webroot（HTTP-01 必填）", existing?.WebRoot ?? system.WebRoots.FirstOrDefault() ?? "", client.Endpoint.IsLoopback);
        var locked = existing?.Certificate.CurrentGeneration is not null;
        dialog.Note("目录属于当前连接的服务器；本机连接可浏览选择，仍需位于管理员批准的根目录内。\n输出根目录：" + string.Join("；", system.OutputRoots));
        var output = dialog.Folder("保存目录 *", existing?.OutputDirectory ?? system.OutputRoots.FirstOrDefault() ?? "", client.Endpoint.IsLoopback, locked);
        var filename = dialog.Text("基础文件名 *（不含扩展名）", existing?.FileName ?? "", readOnly: locked);
        var format = dialog.Choice("文件格式", [("both", "PEM + PFX"), ("pem", "PEM"), ("pfx", "PFX")], existing?.Format ?? "both");
        format.IsEnabled = !locked;
        if (locked) dialog.Note("已有实际证书的配置暂不支持变更输出目录、基础文件名和格式，请新建配置。");
        var auto = dialog.Check("开启自动续期", existing?.AutoRenew ?? true);
        var terms = dialog.Check("我已阅读并接受 Let's Encrypt 订阅协议", existing?.TermsAccepted ?? false);
        var agreement = new Button { Content = "查看 Let's Encrypt 订阅协议 ↗", HorizontalAlignment = HorizontalAlignment.Left };
        agreement.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://letsencrypt.org/repository/") { UseShellExecute = true });
        dialog.Control(agreement);
        // Agreement button remains a native control; all certificate operations use the authenticated API.
        dialog.Note("协议：https://letsencrypt.org/repository/；可在浏览器阅读后勾选。保存只写配置，实际签发需在主界面明确发起。");
        dialog.Submit = async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(email.Text) || string.IsNullOrWhiteSpace(domains.Text)) throw new InvalidOperationException("请填写名称、域名和联系邮箱。");
            var body = new CertificateInput(name.Text.Trim(), domains.Text.Split(['\r', '\n', ',', '，', ';', '；', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries), email.Text.Trim(),
                (string)environment.SelectedValue, (string)validation.SelectedValue, (string?)credential.SelectedValue, webroot.Text.Trim(), output.Text.Trim(), filename.Text.Trim(),
                (string)format.SelectedValue, auto.IsChecked == true, terms.IsChecked == true);
            await client.Write<CertificateItem>(existing is null ? "certificates" : "certificates/" + existing.Id, existing is null ? HttpMethod.Post : HttpMethod.Put, body, existing?.Revision);
        };
        return dialog;
    }
    public static FormDialog Credential(Window owner, PilotClient client, CredentialItem? existing)
    {
        var dialog = new FormDialog(owner, existing is null ? "添加 Cloudflare DNS 凭据" : "替换 DNS Token");
        var name = dialog.Text("凭据名称 *", existing?.Name ?? "");
        var zone = dialog.Text("Zone 域名 *", existing?.ZoneName ?? "", readOnly: existing is not null);
        var id = dialog.Text("Zone ID *（32 位十六进制）", existing?.ZoneId ?? "", readOnly: existing is not null);
        var token = dialog.Password("API Token *");
        dialog.Note("Token 权限限定为该 Zone 的 DNS Edit。密钥只发送到当前服务，加密保存，界面不回读原文。");
        dialog.Submit = async () =>
        {
            await client.Write<object>(existing is null ? "credentials" : "credentials/" + existing.Id, existing is null ? HttpMethod.Post : HttpMethod.Put,
                new { name = name.Text.Trim(), zoneName = zone.Text.Trim(), zoneId = id.Text.Trim(), token = token.Password }, existing?.Revision);
            token.Clear();
        };
        return dialog;
    }
    public static void Details(Window owner, string title, string text)
    {
        var window = new Window { Owner = owner, Title = title, Width = 780, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        window.Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(24), FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI") };
        window.ShowDialog();
    }
}
