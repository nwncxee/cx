using System.Net.Http;
using System.Reflection;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Update;

/// <summary>Declares the release notice on General » About and checks the releases page once a session, on a
/// background thread: a blocking request with a size cap and a timeout, judged against the built-in keys.</summary>
public sealed class UpdateService : IExtenderService
{
    /// <summary>The most a notice or its signature may weigh; a longer answer is dropped unread.</summary>
    private const int MaxBytes = 64 * 1024;
    /// <summary>How long one request may take before the session gives up on it.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        UpdateAbout a = Declare(General.About, Product.Version, OperatingSystem.IsWindows() ? "windows" : "linux", AllowedSigners(), log,
                                Fetch, SelectableText.Copy, GearAttention.Menu);
        new Thread(a.Notice.Check) { IsBackground = true, Name = "cx-update" }.Start();
    }

    /// <summary>Declare the notice's knobs and the release key row on <paramref name="about"/>, over the build's
    /// <paramref name="allowedSigners"/> text, for an install of <paramref name="os"/>'s zip.</summary>
    internal static UpdateAbout Declare(KnobGroup about, string running, string os, string allowedSigners, ExtenderLog log,
                                        Func<string, byte[]?> fetch, Func<string, bool> copy, GearAttention gear)
    {
        UpdateKnobs k = UpdateKnobs.Declare(about);
        var notice = new UpdateNotice(k, running, ReleaseKeys.Parse(allowedSigners), log, fetch, copy, gear) { Os = os };
        return new UpdateAbout(k, notice, ReleaseKeyRow.Declare(about, allowedSigners, copy));
    }

    /// <summary>The release keys built into this extender: <c>packaging/allowed_signers</c>, embedded.</summary>
    private static string AllowedSigners()
    {
        using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("allowed_signers");
        if (s == null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>GET <paramref name="url"/>, following redirects; null on any failure or past <see cref="MaxBytes"/>.</summary>
    private static byte[]? Fetch(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage res = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxBytes) return null;
            using Stream body = res.Content.ReadAsStream();
            using var buf = new MemoryStream();
            byte[] chunk = new byte[8192];
            for (int n; (n = body.Read(chunk)) > 0;)
            {
                if (buf.Length + n > MaxBytes) return null;
                buf.Write(chunk, 0, n);
            }
            return buf.ToArray();
        }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
        catch (OperationCanceledException) { return null; }
    }
}

/// <summary>What About holds for the release notice: its knobs, the notice they act on, and the release key row.</summary>
internal sealed record UpdateAbout(UpdateKnobs Knobs, UpdateNotice Notice, ReleaseKeyRow Key);
