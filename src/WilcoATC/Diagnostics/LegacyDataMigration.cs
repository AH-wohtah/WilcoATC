using System.IO;

namespace WilcoATC.Diagnostics;

/// <summary>
/// Moves the user data folders left behind by the application's former name.
///
/// WHY THIS EXISTS. The application used to store everything under
/// <c>%APPDATA%\FreqWatch</c> and <c>%LOCALAPPDATA%\FreqWatch</c>. Renaming the project to
/// WilcoATC also renames those folders — and everything an existing user has accumulated
/// would silently stop being found: their settings, their controller-editable ATC rule
/// table, and above all the downloaded assets, which are the expensive part. Voice models
/// and the speech-recognition model together weigh hundreds of megabytes and take a long
/// time to fetch; re-downloading them because of a rename would be a poor trade.
///
/// WHEN IT RUNS. Once, at module load, before any other code touches those paths — see
/// <see cref="FileLog.Install"/>, which calls it as its very first statement. Each folder is
/// moved only when the old one exists AND the new one does not, so a user who has already
/// migrated (or who is starting fresh) is never touched, and no file is ever overwritten.
///
/// Failure is not fatal: a migration that cannot happen leaves the application starting on
/// empty folders, exactly as a new install would.
/// </summary>
internal static class LegacyDataMigration
{
    private const string OldName = "FreqWatch";
    private const string NewName = "WilcoATC";

    /// <summary>Result of the migration, kept so the log can report it once it is open.</summary>
    internal static string? Report { get; private set; }

    internal static void Run()
    {
        var moved = new List<string>();

        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData,
                                     Environment.SpecialFolder.LocalApplicationData })
        {
            string basePath = Environment.GetFolderPath(root);
            string from = Path.Combine(basePath, OldName);
            string to = Path.Combine(basePath, NewName);

            try
            {
                if (!Directory.Exists(from) || Directory.Exists(to)) continue;
                Directory.Move(from, to);
                moved.Add(to);
            }
            // A locked file, a folder open in Explorer, a permission problem: none of these
            // justify blocking startup. The user simply starts from empty folders.
            catch (Exception ex)
            {
                moved.Add($"{from} -> FAILED ({ex.GetType().Name}: {ex.Message})");
            }
        }

        if (moved.Count > 0) Report = "[migration] " + string.Join(" ; ", moved);
    }
}
