using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DsInApex.App.Services;

/// <summary>
/// 游戏封面缓存：内存 → 磁盘 → 网络三级。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Common/CoverCacheService.cs</c>（165 行）。
/// <b>与上游的唯一本质差异：图像类型由 WPF 的</b>
/// <c>System.Windows.Media.Imaging.BitmapImage</c> <b>换成 WinUI 3 的</b>
/// <c>Microsoft.UI.Xaml.Media.Imaging.BitmapImage</c>，
/// 且 UI 回调从 <c>Application.Current.Dispatcher</c> 改为 <see cref="DispatcherQueue"/>。
/// </para>
///
/// <para>
/// <b>缓存目录与上游一致</b>：<c>%LOCALAPPDATA%\ApexSenseBridge\cache\covers\&lt;md5(url)&gt;.jpg</c>。
/// 保持同名同格式，是为了让用户从官方版升级后封面不用重新下载。
/// </para>
///
/// <para>
/// 三级策略：内存命中直接返回；磁盘命中 → 立刻返回并加载；都未命中 →
/// 后台下载、写盘、经 UI 线程回调把图交回界面（期间界面显示首字母占位块）。
/// <b>任何失败都静默</b> —— 封面加载绝不能影响游戏库可用性。
/// </para>
/// </summary>
public static class CoverCacheService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApexSenseBridge", "cache", "covers");

    private static readonly ConcurrentDictionary<string, ImageSource> MemoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    static CoverCacheService()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
            {
                Directory.CreateDirectory(CacheDir);
            }
        }
        catch
        {
            // 目录建不出来时退化为「无封面缓存」，不影响功能
        }
    }

    /// <summary>
    /// 取封面。已缓存则立即返回非 null；否则返回 null 并异步回调 <paramref name="onLoaded"/>。
    /// </summary>
    public static ImageSource? GetImage(string? url, Action<ImageSource>? onLoaded)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // 1. 内存缓存
        if (MemoryCache.TryGetValue(url, out ImageSource? memoryImage))
        {
            return memoryImage;
        }

        // 2. 磁盘缓存
        string fileName = GetCacheFileName(url);
        string localPath = Path.Combine(CacheDir, fileName);

        if (File.Exists(localPath))
        {
            try
            {
                if (new FileInfo(localPath).Length > 200)
                {
                    ImageSource? cached = LoadFromFile(localPath);
                    if (cached is not null)
                    {
                        MemoryCache[url] = cached;
                        return cached;
                    }
                }
            }
            catch
            {
                // 磁盘缓存损坏 → 走网络
            }
        }

        // 3. 后台下载（写盘后经 UI 线程回调）
        DispatcherQueue? dispatcher = DispatcherQueue.GetForCurrentThread();
        string capturedUrl = url;

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] data = await HttpClient.GetByteArrayAsync(capturedUrl);
                if (data is { Length: > 200 })
                {
                    try
                    {
                        await File.WriteAllBytesAsync(localPath, data);
                    }
                    catch
                    {
                        // 写盘失败不阻塞显示
                    }

                    void Apply()
                    {
                        ImageSource? loaded = LoadFromFile(localPath);
                        if (loaded is not null)
                        {
                            MemoryCache[capturedUrl] = loaded;
                            onLoaded?.Invoke(loaded);
                        }
                    }

                    if (dispatcher is null || dispatcher.HasThreadAccess)
                    {
                        Apply();
                    }
                    else
                    {
                        dispatcher.TryEnqueue(Apply);
                    }
                }
            }
            catch
            {
                // 静默失败：占位图（首字母块）继续生效
            }
        });

        return null;
    }

    private static ImageSource? LoadFromFile(string path)
    {
        try
        {
            // WinUI 的 BitmapImage 会异步加载 file:// URI，Image 控件在加载完成后自动刷新。
            return new BitmapImage { UriSource = new Uri(path) };
        }
        catch
        {
            return null;
        }
    }

    private static string GetCacheFileName(string url)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(url));
        var sb = new StringBuilder(hash.Length * 2 + 4);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        sb.Append(".jpg");
        return sb.ToString();
    }
}
