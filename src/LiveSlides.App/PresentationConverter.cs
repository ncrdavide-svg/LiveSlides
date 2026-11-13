using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LiveSlides.App;

/// <summary>
/// Converts presentations (.pptx, .ppt, .odp…) to PDF so they can be shown by the same renderer
/// as PDFs. Uses LibreOffice in headless mode (no window appears); if LibreOffice is not
/// installed but PowerPoint is, uses PowerPoint instead. Conversions are cached on disk, keyed
/// by path + size + modified date, so each file is converted only once.
/// </summary>
public static class PresentationConverter
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string CacheFolder => Path.Combine(App.DataFolder, "cache");

    /// <summary>The private LibreOffice profile used for conversions (also the template for the show's profiles).</summary>
    public static string ProfileFolder => Path.Combine(App.DataFolder, "lo-profile");

    public static string? FindSoffice(string? manualPath)
    {
        var candidates = new List<string?> { manualPath };
        foreach (var key in new[] { @"SOFTWARE\LibreOffice\UNO\InstallPath", @"SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath" })
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                if (reg?.GetValue(null) is string dir) candidates.Add(Path.Combine(dir, "soffice.exe"));
            }
            catch { /* registry not readable: fall through to the standard folders */ }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"));
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    /// <returns>Path of the PDF version of <paramref name="sourcePath"/>.</returns>
    /// <param name="engine">AppSettings.PresentationEngine: the conversion uses the same program that plays
    /// the show, so thumbnails and the NEXT preview look like what goes on air.</param>
    public static async Task<string> ConvertAsync(string sourcePath, string? manualSofficePath, string? engine, CancellationToken token)
    {
        var soffice = FindSoffice(manualSofficePath);
        var powerPointType = Type.GetTypeFromProgID("PowerPoint.Application");
        // PowerPoint when it is the program playing the show (see PresentationPlayers.Create).
        var usePowerPoint = powerPointType is not null
                            && (!string.Equals(engine, "libreoffice", StringComparison.OrdinalIgnoreCase) || soffice is null);

        var info = new FileInfo(sourcePath);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{(usePowerPoint ? "pp" : "lo")}")));
        Directory.CreateDirectory(CacheFolder);
        var cached = Path.Combine(CacheFolder, key + ".pdf");
        if (File.Exists(cached)) return cached;

        await Gate.WaitAsync(token);
        try
        {
            if (File.Exists(cached)) return cached;

            var workFolder = Path.Combine(CacheFolder, "work-" + key);
            if (Directory.Exists(workFolder)) Directory.Delete(workFolder, true);
            Directory.CreateDirectory(workFolder);
            try
            {
                var produced = Path.Combine(workFolder, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
                if (usePowerPoint)
                {
                    try
                    {
                        // A dialog PowerPoint shows by itself (repair, protected view...) would hang
                        // it: after 2 minutes LibreOffice takes over, if there is one.
                        await ConvertWithPowerPointAsync(powerPointType!, sourcePath, produced).WaitAsync(TimeSpan.FromMinutes(2), token);
                    }
                    catch (Exception ex) when (soffice is not null && !token.IsCancellationRequested)
                    {
                        App.LogError(ex);
                        foreach (var leftover in Directory.GetFiles(workFolder)) File.Delete(leftover);
                        await ConvertWithLibreOfficeAsync(soffice, sourcePath, workFolder, token);
                    }
                }
                else if (soffice is not null)
                    await ConvertWithLibreOfficeAsync(soffice, sourcePath, workFolder, token);
                else
                    throw new InvalidOperationException("Né PowerPoint né LibreOffice trovati: servono per leggere i PowerPoint");

                if (!File.Exists(produced))
                {
                    // LibreOffice may normalize the output name: accept whatever single PDF it wrote.
                    produced = Directory.GetFiles(workFolder, "*.pdf").FirstOrDefault()
                               ?? throw new InvalidOperationException("conversione non riuscita");
                }
                File.Move(produced, cached, true);
                return cached;
            }
            finally
            {
                try { Directory.Delete(workFolder, true); } catch { /* leftovers are harmless */ }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ConvertWithLibreOfficeAsync(string soffice, string sourcePath, string outputFolder, CancellationToken token)
    {
        // A private LibreOffice profile: with the user's own profile, a LibreOffice window that
        // is already open would swallow the command and nothing would be converted.
        var profile = new Uri(ProfileFolder).AbsoluteUri;
        var start = new ProcessStartInfo(soffice)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add($"-env:UserInstallation={profile}");
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--norestore");
        start.ArgumentList.Add("--nolockcheck");
        start.ArgumentList.Add("--convert-to");
        start.ArgumentList.Add("pdf");
        start.ArgumentList.Add("--outdir");
        start.ArgumentList.Add(outputFolder);
        start.ArgumentList.Add(sourcePath);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("impossibile avviare LibreOffice");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* already gone */ }
            if (token.IsCancellationRequested) throw;
            throw new InvalidOperationException("conversione troppo lunga (oltre 5 minuti)");
        }

        // soffice.exe is a launcher: give the real process a moment to finish writing the file.
        for (var i = 0; i < 60 && !IsPdfComplete(outputFolder); i++)
            await Task.Delay(250, token);
    }

    private static bool IsPdfComplete(string folder)
    {
        var pdf = Directory.GetFiles(folder, "*.pdf").FirstOrDefault();
        if (pdf is null) return false;
        // Still being written if it cannot be opened exclusively.
        try { using var stream = new FileStream(pdf, FileMode.Open, FileAccess.Read, FileShare.None); return stream.Length > 0; }
        catch (IOException) { return false; }
    }

    private static Task ConvertWithPowerPointAsync(Type powerPointType, string sourcePath, string pdfPath)
    {
        // COM automation needs an STA thread of its own.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            dynamic? app = null;
            dynamic? presentation = null;
            try
            {
                app = Activator.CreateInstance(powerPointType)!;
                // Open(FileName, ReadOnly: true, Untitled: false, WithWindow: false); MsoTriState true = -1.
                presentation = app.Presentations.Open(sourcePath, -1, 0, 0);
                presentation.SaveAs(pdfPath, 32 /* ppSaveAsPDF */);
                presentation.Close();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(new InvalidOperationException("PowerPoint: " + ex.Message, ex));
            }
            finally
            {
                // PowerPoint runs as a single instance: it may be playing the show right now, or be
                // the operator's own. Quit only if nothing else is open in it.
                try { if (app is not null && (int)app.Presentations.Count == 0) app.Quit(); } catch { /* best effort */ }
                // Release the COM references at once, so PowerPoint does not linger in the background.
                try { if (presentation is not null) Marshal.FinalReleaseComObject(presentation); } catch { /* already released */ }
                try { if (app is not null) Marshal.FinalReleaseComObject(app); } catch { /* already released */ }
            }
        })
        { IsBackground = true, Name = "PowerPoint export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
