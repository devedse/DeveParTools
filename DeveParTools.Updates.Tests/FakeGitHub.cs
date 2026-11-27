using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeveParTools.Updates.Tests;

/// <summary>
/// A stand-in for GitHub: answers the "latest release" request and the downloads from a table, and counts requests.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string LatestUrl = "https://api.github.com/repos/devedse/DeveParTools/releases/latest";

    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

    public int Requests { get; private set; }

    public List<HttpRequestMessage> Seen { get; } = [];

    public void Route(string url, Func<HttpResponseMessage> respond) => _routes[url] = respond;

    public void Json(string url, string json) => Route(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public void Bytes(string url, byte[] data, bool withLength = true)
        => Route(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = withLength ? new ByteArrayContent(data) : new UnknownLengthContent(data) });

    public void Status(string url, HttpStatusCode code) => Route(url, () => new HttpResponseMessage(code) { Content = new StringContent("{}") });

    public void Fail(string url, Exception e) => Route(url, () => throw e);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        Seen.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return _routes.TryGetValue(request.RequestUri!.ToString(), out var respond)
            ? Task.FromResult(respond())
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    /// <summary>A release as GitHub's API returns it (only the fields the updater reads, plus a few it ignores).</summary>
    public static string ReleaseJson(string tag, params (string Name, byte[] Data, string? Digest)[] assets)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["name"] = "DeveParTools " + tag,
            ["html_url"] = $"https://github.com/devedse/DeveParTools/releases/tag/{tag}",
            ["prerelease"] = false,
            ["assets"] = assets.Select(a => new Dictionary<string, object?>
            {
                ["name"] = a.Name,
                ["browser_download_url"] = DownloadUrl(tag, a.Name),
                ["size"] = a.Data.LongLength,
                ["digest"] = a.Digest,
            }).ToList(),
        });

    public static string DownloadUrl(string tag, string name) => $"https://github.com/devedse/DeveParTools/releases/download/{tag}/{name}";

    public static string Sha256Digest(byte[] data) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>A zip as CI builds it on Windows (7z of the publish folder): files at the top level.</summary>
    public static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                if (!name.EndsWith('/'))
                {
                    using var w = new StreamWriter(entry.Open());
                    w.Write(content);
                }
            }
        }

        return ms.ToArray();
    }

    /// <summary>A tar.gz as CI builds it (tar -C publish -czf x.tar.gz .): a "./" folder entry, then "./name" files.</summary>
    public static byte[] TarGz(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                };
                tar.WriteEntry(entry);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Content without a Content-Length header (a chunked download).</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
