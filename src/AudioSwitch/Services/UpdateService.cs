using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudioSwitch.Services;

/// <summary>一个可用的 GitHub Releases 版本信息。</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string ReleaseUrl, string? SetupUrl, string Notes)
{
    public string DisplayVersion => "v" + Version.ToString(3);

    public string Summary
    {
        get
        {
            string text = Notes.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
            int end = text.IndexOf('\n');
            return end > 0 ? text[..end].Trim() : text;
        }
    }
}

/// <summary>通过 GitHub Releases 检查、下载并启动新版本安装包。</summary>
internal static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/yaanlaan/AudioSwitch/releases/latest";
    private const string SetupPrefix = "AudioSwitch-Setup-";
    private const string SetupSuffix = ".exe";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0, 0));

    /// <summary>读取最新 Release；无 Release 或网络失败时抛异常。</summary>
    public static async Task<UpdateInfo> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
                request.Headers.UserAgent.ParseAdd($"AudioSwitch/{CurrentVersion.ToString(3)}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                return request;
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        JsonElement root = document.RootElement;
        string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;

        if (!TryParseVersion(tag, out Version version))
        {
            throw new InvalidDataException($"无法解析版本号：{tag}");
        }

        string releaseUrl = root.TryGetProperty("html_url", out JsonElement urlElement)
            ? urlElement.GetString() ?? string.Empty
            : string.Empty;

        string notes = root.TryGetProperty("body", out JsonElement bodyElement)
            ? bodyElement.GetString() ?? string.Empty
            : string.Empty;

        string? setupUrl = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;

                if (name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(SetupSuffix, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out JsonElement downloadElement))
                {
                    setupUrl = downloadElement.GetString();
                    if (!string.IsNullOrWhiteSpace(setupUrl))
                    {
                        break;
                    }
                }
            }
        }

        return new UpdateInfo(version, tag, releaseUrl, setupUrl, notes);
    }

    public static bool IsNewer(UpdateInfo info, Version current) => Normalize(info.Version) > Normalize(current);

    /// <summary>下载安装包；返回本地路径。targetDirectory 为空时下载到 %LOCALAPPDATA%\AudioSwitch\updates。</summary>
    public static async Task<string> DownloadSetupAsync(
        UpdateInfo info,
        string? targetDirectory = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.SetupUrl))
        {
            throw new InvalidOperationException("该版本没有可下载的安装包，请打开 Release 页面手动下载。");
        }

        string directory = targetDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioSwitch",
            "updates");
        Directory.CreateDirectory(directory);

        string fileName = Path.GetFileName(new Uri(info.SetupUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{SetupPrefix}{info.Version.ToString(3)}{SetupSuffix}";
        }

        string path = Path.Combine(directory, fileName);
        string partPath = path + ".part";
        if (File.Exists(partPath))
        {
            File.Delete(partPath);
        }

        using HttpResponseMessage response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, info.SetupUrl),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        long received = 0;
        byte[] buffer = new byte[81920];

        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total is > 0)
                {
                    progress?.Report((double)received / total.Value);
                }
            }
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(partPath, path);
        return path;
    }

    /// <summary>用系统默认方式启动安装程序（会触发 UAC）。</summary>
    public static void LaunchInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenReleasePage(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://github.com/yaanlaan/AudioSwitch/releases/latest";
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>解析 v1.2.4 / 1.2.4 / 1.2 形式的版本号，统一成四段。</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim().TrimStart('v', 'V');
        int dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            trimmed = trimmed[..dash];
        }

        if (!Version.TryParse(trimmed, out Version? parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    public static Version Normalize(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using HttpRequestMessage request = requestFactory();
                return await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 3 && (ex is HttpRequestException or IOException))
            {
                lastError = ex;
                Log.Warn($"网络请求失败（第 {attempt} 次），将重试：{ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new HttpRequestException("网络请求失败");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AudioSwitch/{CurrentVersion.ToString(3)}");
        return client;
    }
}
