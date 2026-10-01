// zipfill: porovna obsah zipu s rozbalenou slozkou a doplni jen chybejici soubory.
// Pouziti: zipfill scan|extract|verify <zip> <cilovaSlozka> <slozkaProReporty> [--prazdny-cil]
//   scan    - jen report (nic nezapisuje do cile)
//   extract - doplni chybejici soubory; nikdy neprepisuje, zapisuje pres docasny soubor a overuje CRC32
//   verify  - jako scan + u existujicich souboru se stejnou velikosti spocita CRC32 a porovna se zipem
// <cilovaSlozka> = slozka, DO KTERE se zip rozbaloval (ne slozka, kterou zip vytvoril).
// <slozkaProReporty> nesmi byt cilova slozka ani uvnitr ni; kazdy beh v ni vytvori novou podslozku
// <rezim>-<cas>-<nahodne> a reporty nikdy neprepisuje.
// Navratove kody: 0 = v rozsahu rezimu nezjisteny zadne rozdily (obsah stejne velkych souboru kontroluje jen verify)
//                 1 = neco chybi / lisi se / selhalo / problem se jmenem
//                 2 = spatne argumenty
//                 3 = extract neproveden: na disku neni nic ze zipu (nejspis spatna cilova slozka);
//                     obejiti pro umyslne rozbaleni do prazdne slozky: --prazdny-cil
//                 4 = fatalni chyba (zip nejde otevrit, nelze zapsat reporty, ...)
using System.IO.Compression;
using System.Text;
using Crc32 = System.IO.Hashing.Crc32;

try
{
    return Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATALNI CHYBA (kod 4): {ex.GetType().Name}: {ex.Message}");
    return 4;
}

static int Run(string[] args)
{
    var positional = args.Where(a => !a.StartsWith("--")).ToArray();
    var flags = args.Where(a => a.StartsWith("--")).ToHashSet();
    if (positional.Length != 4 || positional[0] is not ("scan" or "extract" or "verify") || flags.Except(["--prazdny-cil"]).Any())
    {
        Console.Error.WriteLine("Pouziti: zipfill scan|extract|verify <zip> <cilovaSlozka> <slozkaProReporty> [--prazdny-cil]");
        return 2;
    }

    var mode = positional[0];
    var zipPath = positional[1];
    var destRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(positional[2])); // koren "D:\" zustava
    var reportBase = Path.GetFullPath(positional[3]);
    var allowEmptyDest = flags.Contains("--prazdny-cil");
    if (IsSameOrInside(reportBase, destRoot))
    {
        Console.Error.WriteLine("Slozka pro reporty nesmi byt cilova slozka ani uvnitr ni (reporty nepatri do projektu).");
        return 2;
    }
    // Kazdy beh pise do nove unikatni podslozky a soubory otevira s CreateNew: reporty nikdy nic neprepisou,
    // ani kdyby slozka pro reporty byla aliasem cile (junction, \\?\ zapis) - tomu textova kontrola vyse nezabrani.
    var reportDir = Path.Combine(reportBase, $"{mode}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
    if (Directory.Exists(reportDir)) throw new IOException($"Slozka pro reporty uz existuje: {reportDir}");
    Directory.CreateDirectory(reportDir);

    const int ExplorerMaxPath = 259; // MAX_PATH 260 vcetne koncove nuly
    char[] invalidChars = ['<', '>', ':', '"', '|', '?', '*'];
    var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
    for (int i = 1; i <= 9; i++) { reserved.Add($"COM{i}"); reserved.Add($"LPT{i}"); }

    var missing = new List<(ZipArchiveEntry E, string Rel, string Full)>();
    var sameSize = new List<(ZipArchiveEntry E, string Rel, string Full)>();
    var missingDirs = new List<string>();
    var mismatch = new List<string>();
    var problems = new List<string>();
    var symlinks = new List<string>();
    var macJunk = new List<string>();
    var skipped = new List<string>();
    var nfdNames = new List<string>();
    var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var topLevel = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var missingByPrefix = new SortedDictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
    var longByPrefix = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var reparseCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    long files = 0, dirs = 0, existingDirs = 0, totalBytes = 0, missingBytes = 0, tooLongForExplorer = 0;
    long nonAsciiExisting = 0, nonAsciiMissing = 0;
    int maxEntryLen = 0, maxFullLen = 0;
    string longestEntry = "";

    using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: Encoding.UTF8);

    // Unity projekty v zipu (podle ProjectSettings/ProjectVersion.txt): jejich Temp a Logs Unity samo maze/prepisuje.
    var unityRoots = zip.Entries
        .Select(e => e.FullName.Replace('\\', '/').Normalize(NormalizationForm.FormC))
        .Where(n => n == "ProjectSettings/ProjectVersion.txt" || n.EndsWith("/ProjectSettings/ProjectVersion.txt"))
        .Select(n => n[..^"ProjectSettings/ProjectVersion.txt".Length])
        .ToList();

    foreach (var e in zip.Entries)
    {
        var name = e.FullName.Replace('\\', '/');
        var isDir = name.EndsWith('/');
        var rel = name.TrimEnd('/');
        if (rel.Length == 0) continue;

        var segments = rel.Split('/');
        if (rel.StartsWith('/') || segments.Any(s => s is "" or ".." or "."))
        {
            problems.Add($"NEBEZPECNA CESTA (preskoceno): {name}");
            continue;
        }

        // Zip z macOS mivá jmena v NFD; Pruzkumnik je zapisuje v NFC a v NFC je ma i git index.
        // Cil proto pocitame v NFC, jinak by vznikly duplicitni soubory (a v Unity duplicitni GUID).
        var nfc = rel.Normalize(NormalizationForm.FormC);
        if (!string.Equals(rel, nfc, StringComparison.Ordinal))
        {
            nfdNames.Add(rel);
            rel = nfc;
            segments = rel.Split('/');
        }

        if (segments[0] == "__MACOSX" || segments[^1].StartsWith("._"))
        {
            if (!isDir) macJunk.Add(rel);
            continue;
        }
        topLevel[segments[0]] = topLevel.GetValueOrDefault(segments[0]) + 1;

        var skipReason = SkipReason(segments, rel, isDir);
        if (skipReason != null)
        {
            if (!isDir) skipped.Add($"{skipReason}\t{rel}");
            continue;
        }

        var bad = segments.FirstOrDefault(s =>
            s.IndexOfAny(invalidChars) >= 0 || s.EndsWith(' ') || s.EndsWith('.') || s.Any(c => c < 32) ||
            s.Length > 255 || reserved.Contains(s.Split('.')[0].TrimEnd(' ')));
        if (bad != null)
        {
            problems.Add($"NEPLATNE JMENO PRO WINDOWS '{bad}' (preskoceno): {name}");
            continue;
        }

        var full = Path.Combine(destRoot, rel.Replace('/', '\\'));
        if (rel.Length > maxEntryLen) { maxEntryLen = rel.Length; longestEntry = rel; }
        maxFullLen = Math.Max(maxFullLen, full.Length);

        var prefix = string.Join('/', segments.Take(Math.Min(4, segments.Length - (isDir ? 0 : 1))));
        if (full.Length > ExplorerMaxPath)
        {
            tooLongForExplorer++;
            longByPrefix[prefix] = longByPrefix.GetValueOrDefault(prefix) + 1;
        }

        if (targets.TryGetValue(rel, out var other))
        {
            if (!(isDir && other.EndsWith('/')))
                problems.Add($"KOLIZE CILU (stejne jmeno po NFC / jen velikost pismen): '{other}' <-> '{name}'");
            continue;
        }
        targets[rel] = name;

        if (isDir)
        {
            dirs++;
            if (File.Exists(full)) problems.Add($"NA DISKU JE SOUBOR MISTO SLOZKY: {rel}");
            else if (Directory.Exists(full)) existingDirs++;
            else
            {
                var rpDir = ReparseOnPath(Path.GetDirectoryName(full)!);
                if (rpDir != null) problems.Add($"NA CESTE JE JUNCTION/SYMLINK '{rpDir}' (slozka preskocena): {rel}");
                else missingDirs.Add(full);
            }
            continue;
        }

        files++;
        totalBytes += e.Length;

        var unixMode = (e.ExternalAttributes >> 16) & 0xFFFF;
        if ((unixMode & 0xF000) == 0xA000)
        {
            symlinks.Add(rel);
            continue;
        }

        var nonAscii = rel.Any(c => c > 127);
        var fi = new FileInfo(full);
        if (!fi.Exists)
        {
            if (Directory.Exists(full)) { problems.Add($"NA DISKU JE SLOZKA MISTO SOUBORU: {rel}"); continue; }
            var reparse = ReparseOnPath(Path.GetDirectoryName(full)!);
            if (reparse != null) { problems.Add($"NA CESTE JE JUNCTION/SYMLINK '{reparse}' (preskoceno): {rel}"); continue; }
            if (nonAscii) nonAsciiMissing++;
            missing.Add((e, rel, full));
            missingBytes += e.Length;
            var cur = missingByPrefix.GetValueOrDefault(prefix);
            missingByPrefix[prefix] = (cur.Count + 1, cur.Bytes + e.Length);
        }
        else
        {
            if (nonAscii) nonAsciiExisting++;
            if (fi.Length != e.Length)
                mismatch.Add($"{rel}\tzip={e.Length}\tdisk={fi.Length}\tdiskZmeneno={fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            else
                sameSize.Add((e, rel, full));
        }
    }

    var presentOnDisk = sameSize.Count + mismatch.Count + existingDirs;
    var wrongRootSuspected = presentOnDisk == 0 && missing.Count + missingDirs.Count > 0;

    var sb = new StringBuilder();
    sb.AppendLine($"Rezim: {mode}");
    sb.AppendLine($"Zip: {zipPath}");
    sb.AppendLine($"Cil: {destRoot} (delka vc. koncoveho \\ = {Path.Combine(destRoot, "x").Length - 1})");
    sb.AppendLine($"Reporty: {reportDir}");
    sb.AppendLine("Nejvyssi uroven v zipu (bez __MACOSX): " + string.Join(", ", topLevel.Select(kv => $"'{kv.Key}' ({kv.Value})")));
    sb.AppendLine("Unity projekty v zipu: " + (unityRoots.Count == 0 ? "zadny" : string.Join(", ", unityRoots.Select(r => $"'{r}'"))));
    sb.AppendLine($"Polozek v zipu (bez preskocenych): soubory {files}, slozky {dirs}, nekomprimovane {totalBytes / 1024.0 / 1024 / 1024:F2} GB");
    sb.AppendLine($"Soubory na disku se stejnou velikosti: {sameSize.Count}");
    sb.AppendLine($"Chybejici soubory: {missing.Count} ({missingBytes / 1024.0 / 1024:F1} MB)");
    sb.AppendLine($"Chybejici slozky: {missingDirs.Count}");
    sb.AppendLine($"Soubory s jinou velikosti nez v zipu (jen report, neprepisuje se): {mismatch.Count}");
    sb.AppendLine($"Symlinky v zipu (neresi se): {symlinks.Count}");
    sb.AppendLine($"macOS smeti (__MACOSX, ._*) - vynechano: {macJunk.Count}");
    sb.AppendLine($"Umyslne preskoceno (.DS_Store, .git/index.lock, .git/lfs/tmp, Unity Temp/Logs): {skipped.Count}");
    sb.AppendLine($"Jmena v NFD (rozlozena diakritika z macOS), mapovano na NFC: {nfdNames.Count}");
    sb.AppendLine($"Kontrola kodovani: jmena s diakritikou na disku nalezena {nonAsciiExisting}, chybi {nonAsciiMissing}");
    sb.AppendLine($"Problemy: {problems.Count}");
    sb.AppendLine($"Nejdelsi polozka v zipu: {maxEntryLen} znaku: {longestEntry}");
    sb.AppendLine($"Nejdelsi plna cesta: {maxFullLen} znaku; polozek nad {ExplorerMaxPath}: {tooLongForExplorer}");
    sb.AppendLine($"Aby Pruzkumnik rozbalil vse, smi mit cilova slozka (vc. koncoveho \\) nejvyse {ExplorerMaxPath - maxEntryLen} znaku.");
    if (wrongRootSuspected)
    {
        sb.AppendLine();
        sb.AppendLine("!!! POZOR: na disku neni NIC ze zipu. Nejspis je cilova slozka o uroven vedle.");
        sb.AppendLine("!!! Cilova slozka ma byt ta, DO KTERE se zip rozbaloval (musi obsahovat slozky z 'Nejvyssi uroven v zipu').");
        sb.AppendLine("!!! Pro umyslne rozbaleni do prazdne slozky pouzij --prazdny-cil.");
    }
    sb.AppendLine();
    sb.AppendLine("Chybejici soubory podle slozky:");
    foreach (var (k, v) in missingByPrefix) sb.AppendLine($"  {v.Count,7}  {v.Bytes / 1024.0 / 1024,9:F1} MB  {k}");
    sb.AppendLine();
    sb.AppendLine("Polozky s cestou nad limit Pruzkumniku podle slozky:");
    foreach (var (k, v) in longByPrefix) sb.AppendLine($"  {v,7}  {k}");

    Report("missing", missing.Select(m => m.Rel));
    Report("missing-dirs", missingDirs);
    Report("mismatch", mismatch);
    Report("problems", problems);
    Report("symlinks", symlinks);
    Report("macjunk", macJunk);
    Report("skipped", skipped);
    Report("nfd", nfdNames);

    var buffer = new byte[1 << 20];
    var exitCode = 0;

    if (mode == "extract" && wrongRootSuspected && !allowEmptyDest)
    {
        sb.AppendLine();
        sb.AppendLine("EXTRACT NEPROVEDEN (kod 3): do cilove slozky nebylo nic zapsano (reporty jsou ve slozce pro reporty).");
        WriteNew($"{mode}-summary.txt", sb.ToString());
        Console.Write(sb.ToString());
        return 3;
    }

    if (mode == "extract")
    {
        int done = 0, failed = 0, dirsCreated = 0, dirsFailed = 0;
        var log = new List<string>();
        foreach (var dir in missingDirs)
        {
            try { Directory.CreateDirectory(dir); dirsCreated++; }
            catch (Exception ex) { dirsFailed++; log.Add($"CHYBA SLOZKA\t{dir}\t{ex.GetType().Name}: {ex.Message}"); }
        }
        foreach (var (e, rel, full) in missing)
        {
            // kratke unikatni docasne jmeno ve stejne slozce (nezavisle na delce jmena souboru);
            // mazeme jen soubor, ktery prokazatelne vytvoril tento beh
            var tmp = Path.Combine(Path.GetDirectoryName(full)!, $".zf-{Guid.NewGuid().ToString("N")[..12]}.tmp");
            var tmpCreated = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var crc = new Crc32();
                long written = 0;
                using (var src = e.Open())
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    tmpCreated = true;
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        crc.Append(buffer.AsSpan(0, n));
                        dst.Write(buffer, 0, n);
                        written += n;
                    }
                }
                var got = crc.GetCurrentHashAsUInt32();
                if (got != e.Crc32 || written != e.Length)
                    throw new InvalidDataException($"CRC/velikost nesedi: crc {got:X8} vs {e.Crc32:X8}, velikost {written} vs {e.Length}");
                File.Move(tmp, full, overwrite: false);
                tmpCreated = false;
                File.SetLastWriteTime(full, e.LastWriteTime.LocalDateTime);
                done++;
                log.Add($"OK\t{rel}");
            }
            catch (Exception ex)
            {
                failed++;
                if (tmpCreated) { try { File.Delete(tmp); } catch { } }
                log.Add($"CHYBA\t{rel}\t{ex.GetType().Name}: {ex.Message}");
            }
            if ((done + failed) % 500 == 0) Console.WriteLine($"  ... {done + failed}/{missing.Count}");
        }
        Report("log", log);
        sb.AppendLine();
        sb.AppendLine($"DOPLNENO: {done}, SELHALO: {failed}, slozky vytvoreny: {dirsCreated}, slozky selhaly: {dirsFailed}");
        if (failed > 0 || dirsFailed > 0) exitCode = 1;
    }
    else if (missing.Count > 0 || missingDirs.Count > 0)
    {
        exitCode = 1;
    }

    if (mode == "verify")
    {
        long checkedFiles = 0, checkedBytes = 0;
        var crcBad = new List<string>();
        var lastReport = DateTime.UtcNow;
        foreach (var (e, rel, full) in sameSize)
        {
            try
            {
                var crc = new Crc32();
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                {
                    int n;
                    while ((n = fs.Read(buffer, 0, buffer.Length)) > 0) crc.Append(buffer.AsSpan(0, n));
                }
                var got = crc.GetCurrentHashAsUInt32();
                if (got != e.Crc32)
                    crcBad.Add($"{rel}\tzipCrc={e.Crc32:X8}\tdiskCrc={got:X8}\tdiskZmeneno={File.GetLastWriteTime(full):yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                crcBad.Add($"{rel}\tNELZE PRECIST: {ex.GetType().Name}: {ex.Message}");
            }
            checkedFiles++;
            checkedBytes += e.Length;
            if ((DateTime.UtcNow - lastReport).TotalSeconds > 30)
            {
                lastReport = DateTime.UtcNow;
                Console.WriteLine($"  ... {checkedFiles}/{sameSize.Count} souboru, {checkedBytes / 1024.0 / 1024 / 1024:F1} GB");
            }
        }
        Report("crc-bad", crcBad);
        sb.AppendLine();
        sb.AppendLine($"CRC OVERENO: {checkedFiles} souboru ({checkedBytes / 1024.0 / 1024 / 1024:F2} GB), NESHODA/NECITELNE: {crcBad.Count}");
        sb.AppendLine("(Neshoda u souboru zmeneneho po rozbaleni muze byt bezna editace - over v klidu, neprijimej automaticky.)");
        if (crcBad.Count > 0) exitCode = 1;
    }

    if (mismatch.Count > 0 || problems.Count > 0 || (wrongRootSuspected && mode != "extract")) exitCode = 1;
    sb.AppendLine();
    sb.AppendLine($"NAVRATOVY KOD: {exitCode}");
    WriteNew($"{mode}-summary.txt", sb.ToString());
    Console.Write(sb.ToString());
    return exitCode;

    void Report(string file, IEnumerable<string> lines) =>
        WriteNew($"{mode}-{file}.txt", string.Concat(lines.Select(l => l + Environment.NewLine)));

    void WriteNew(string fileName, string text)
    {
        using var fs = new FileStream(Path.Combine(reportDir, fileName), FileMode.CreateNew, FileAccess.Write);
        using var w = new StreamWriter(fs, new UTF8Encoding(false));
        w.Write(text);
    }

    static bool IsSameOrInside(string path, string root)
    {
        static string Norm(string p)
        {
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
            p = Path.TrimEndingDirectorySeparator(p);
            return p.EndsWith('\\') ? p : p + "\\";
        }
        return Norm(path).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase);
    }

    string? SkipReason(string[] segs, string relPath, bool isDirectory)
    {
        var last = segs[^1];
        if (!isDirectory && last == ".DS_Store") return "macOS .DS_Store";
        if (!isDirectory && last == "index.lock" && segs.Length >= 2 && segs[^2] == ".git") return "zatuchly .git/index.lock";
        for (int i = 0; i + 2 < segs.Length; i++)
            if (segs[i] == ".git" && segs[i + 1] == "lfs" && segs[i + 2] == "tmp") return "git-lfs tmp";
        foreach (var root in unityRoots)
            foreach (var d in new[] { "Temp", "Logs" })
                if (relPath == root + d || relPath.StartsWith(root + d + "/", StringComparison.Ordinal)) return $"Unity {d}";
        return null;
    }

    string? ReparseOnPath(string dir)
    {
        // projde existujici slozky od korene disku/sdileni smerem dolu; vrati prvni reparse point (junction/symlink)
        if (reparseCache.TryGetValue(dir, out var cached)) return cached ? dir : ParentReparse(dir);
        var parentHit = ParentReparse(dir);
        if (parentHit != null) { reparseCache[dir] = false; return parentHit; }
        var di = new DirectoryInfo(dir);
        var isRp = di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint);
        reparseCache[dir] = isRp;
        return isRp ? dir : null;
    }

    string? ParentReparse(string dir)
    {
        var parent = Path.GetDirectoryName(dir); // null u korene ("D:\", "\\server\share")
        return parent == null ? null : ReparseOnPath(parent);
    }
}
