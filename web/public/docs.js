document.getElementById('try').addEventListener('submit', async event => {
  event.preventDefault(); const result = document.getElementById('result');
  try { const response = await fetch(document.getElementById('endpoint').value); result.textContent = `HTTP ${response.status}\n${JSON.stringify(await response.json(), null, 2)}`; }
  catch { result.textContent = '无法读取接口响应，请检查服务和登录状态。'; }
});
