using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Compare;

/// <summary>
/// File Shared 로 공유 (Capture 와 같은 방식): 이 PC 의 File Shared 서비스(포트 5000)에 파일을 올리고 받은 짧은 링크를 돌려줌.
/// 링크 주소는 File Shared 화면과 같은 규칙으로 고른 이 PC 의 IPv4 (가상 · VPN 어댑터 제외)
/// </summary>
public static class FileShared
{
    private const int Port = 5000;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<string> ShareAsync(string path, CancellationToken cancel = default)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // File Shared 의 업로드와 같게: filename* (UTF-8)
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileNameStar = Path.GetFileName(path) };
        form.Add(file);

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync($"http://127.0.0.1:{Port}/upload", form, cancel);
        }
        catch (HttpRequestException error)
        {
            throw new FileSharedException("File Shared 가 켜져 있지 않습니다. i-luncher 에서 File Shared 를 설치하고 한 번 실행하세요.", error);
        }
        using (response)
        {
            string json = await response.Content.ReadAsStringAsync(cancel);
            string? code = null, message = null;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("short_code", out var shortCode)) code = shortCode.GetString();
                if (document.RootElement.TryGetProperty("error", out var error)) message = error.GetString();
            }
            catch (JsonException) { }
            if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(code))
                throw new FileSharedException("File Shared 에 올리지 못했습니다" + (message is { Length: > 0 } ? ": " + message : $" ({(int)response.StatusCode})."));
            return $"http://{LocalAddress()}:{Port}/s/{code}";
        }
    }

    /// <summary>이더넷 · 무선 어댑터의 첫 IPv4 (File Shared 와 같은 규칙)</summary>
    public static string LocalAddress()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
                string name = (adapter.Description + " " + adapter.Name).ToLowerInvariant();
                if (new[] { "virtual", "vmware", "hyper-v", "vpn", "vethernet", "tap", "loopback" }.Any(name.Contains)) continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork) return address.Address.ToString();
            }
        }
        catch (NetworkInformationException) { }
        return "127.0.0.1";
    }
}

public sealed class FileSharedException(string message, Exception? inner = null) : Exception(message, inner);
