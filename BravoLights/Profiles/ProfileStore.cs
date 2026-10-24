using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using NLog;

namespace BravoLights.Profiles
{
    /// <summary>
    /// How to handle an imported profile whose id already exists in the library.
    /// </summary>
    public enum ImportConflictMode
    {
        /// <summary>Replace the existing profile.</summary>
        Replace,
        /// <summary>Keep the existing profile and add the import as a copy.</summary>
        KeepBoth
    }

    /// <summary>
    /// The user's profile library: one .bblprofile file per profile in a directory.
    /// </summary>
    public sealed class ProfileStore : IDisposable
    {
        public const string ProfileExtension = ".bblprofile";
        public const string PackExtension = ".bblpack";

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            // Keep expressions readable: don't escape <, >, & etc.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly object sync = new();
        private readonly Timer reloadTimer;
        private FileSystemWatcher watcher;
        private List<Profile> profiles = new();

        /// <summary>
        /// Set while we're writing files ourselves, so that we don't reload in response to our own changes.
        /// </summary>
        private DateTime suppressWatcherUntil = DateTime.MinValue;

        public ProfileStore(string directory)
        {
            Directory = directory;
            reloadTimer = new Timer(_ => ReloadFromWatcher(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public string Directory { get; }

        /// <summary>
        /// Raised (on a background thread) whenever the set of profiles or their content changes.
        /// </summary>
        public event EventHandler ProfilesChanged;

        public IReadOnlyList<Profile> Profiles
        {
            get
            {
                lock (sync)
                {
                    return profiles.ToList();
                }
            }
        }

        public Profile DefaultProfile
        {
            get
            {
                lock (sync)
                {
                    return profiles.FirstOrDefault(p => p.IsDefault);
                }
            }
        }

        public Profile Find(string id)
        {
            lock (sync)
            {
                return profiles.FirstOrDefault(p => p.Id == id);
            }
        }

        public void Load()
        {
            System.IO.Directory.CreateDirectory(Directory);

            var loaded = new List<Profile>();
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + ProfileExtension))
            {
                try
                {
                    var profile = ReadProfileFile(file);
                    profile.FilePath = file;
                    loaded.Add(profile);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Failed to read profile {0}", file);
                }
            }

            // Two files may claim the same id (e.g. a file copied by hand). Keep the first and give the others new ids.
            var seen = new HashSet<string>();
            foreach (var p in loaded)
            {
                if (!seen.Add(p.Id))
                {
                    p.Id = Guid.NewGuid().ToString("N");
                }
            }

            // Only one profile may be the default.
            var defaults = loaded.Where(p => p.IsDefault).ToList();
            foreach (var extra in defaults.Skip(1))
            {
                extra.IsDefault = false;
            }

            lock (sync)
            {
                profiles = loaded.OrderBy(p => p.IsDefault ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }

            logger.Info("Loaded {0} profiles from {1}", loaded.Count, Directory);
        }

        public void StartWatching()
        {
            watcher = new FileSystemWatcher(Directory, "*" + ProfileExtension)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += Watcher_Changed;
            watcher.Created += Watcher_Changed;
            watcher.Deleted += Watcher_Changed;
            watcher.Renamed += Watcher_Changed;
            watcher.EnableRaisingEvents = true;
        }

        private void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (DateTime.UtcNow < suppressWatcherUntil)
            {
                return;
            }
            // Debounce: editors often write files in several steps
            reloadTimer.Change(300, Timeout.Infinite);
        }

        private void ReloadFromWatcher()
        {
            logger.Debug("Profiles directory changed externally; reloading");
            Load();
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Saves a profile, creating or replacing its file.
        /// </summary>
        public Profile Save(Profile profile)
        {
            var copy = profile.Clone();
            copy.Normalise();

            lock (sync)
            {
                // We may delete/rename files below as well as writing; none of that should trigger a reload
                SuppressWatcher();

                if (copy.IsDefault)
                {
                    // Demote any other default
                    foreach (var other in profiles.Where(p => p.IsDefault && p.Id != copy.Id).ToList())
                    {
                        other.IsDefault = false;
                        WriteFile(other, other.FilePath);
                    }
                }

                var existing = profiles.FirstOrDefault(p => p.Id == copy.Id);
                var desiredPath = Path.Combine(Directory, MakeFileName(copy));

                // If the name changed, rename the file
                if (existing?.FilePath != null && !string.Equals(existing.FilePath, desiredPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(desiredPath))
                    {
                        TryDelete(existing.FilePath);
                    }
                    else
                    {
                        // Something else already has that name; keep the old file name
                        desiredPath = existing.FilePath;
                    }
                }
                else if (existing == null && File.Exists(desiredPath))
                {
                    desiredPath = UniquePath(desiredPath);
                }
                else if (existing?.FilePath != null)
                {
                    desiredPath = existing.FilePath;
                }

                WriteFile(copy, desiredPath);
                copy.FilePath = desiredPath;

                profiles.RemoveAll(p => p.Id == copy.Id);
                profiles.Add(copy);
                profiles = profiles.OrderBy(p => p.IsDefault ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }

            RaiseChanged();
            return copy;
        }

        public void Delete(string id)
        {
            lock (sync)
            {
                var existing = profiles.FirstOrDefault(p => p.Id == id);
                if (existing == null)
                {
                    return;
                }
                if (existing.IsDefault)
                {
                    throw new InvalidOperationException("The default profile can't be deleted.");
                }
                SuppressWatcher();
                if (existing.FilePath != null)
                {
                    TryDelete(existing.FilePath);
                }
                profiles.Remove(existing);
            }
            RaiseChanged();
        }

        /// <summary>
        /// Creates a new, unsaved copy of a profile with a fresh id.
        /// </summary>
        public static Profile Duplicate(Profile source, string newName = null)
        {
            var copy = source.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = newName ?? source.Name + " (copy)";
            copy.IsDefault = false;
            copy.FilePath = null;
            copy.TemplateId = source.TemplateId ?? (source.IsDefault ? null : source.Id);
            return copy;
        }

        /// <summary>
        /// Imports profiles, returning the profiles that were added or replaced.
        /// </summary>
        public IList<Profile> Import(IEnumerable<Profile> incoming, ImportConflictMode mode)
        {
            var results = new List<Profile>();
            foreach (var profile in incoming)
            {
                var p = profile.Clone();
                p.FilePath = null;

                var existing = Find(p.Id);
                if (existing != null && mode == ImportConflictMode.KeepBoth)
                {
                    p.Id = Guid.NewGuid().ToString("N");
                    p.Name = $"{p.Name} (imported)";
                    p.IsDefault = false;
                }
                else if (existing == null && p.IsDefault && DefaultProfile != null)
                {
                    // Don't silently replace the user's default with an imported one under a different id; add it as a normal profile.
                    p.IsDefault = false;
                    p.Name = $"{p.Name} (imported default)";
                }

                results.Add(Save(p));
            }
            return results;
        }

        /// <summary>
        /// Reads profiles from a file: a .bblprofile, a .bblpack, or a legacy .ini configuration.
        /// </summary>
        public static IList<Profile> ReadImportFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ini" || ext == ".txt")
            {
                return LegacyIniImporter.ImportFile(path);
            }

            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<Profile>>(text, JsonOptions).Select(Validate).ToList();
            }

            var format = root.TryGetProperty("format", out var formatEl) ? formatEl.GetString() : null;
            if (format == ProfilePack.FormatName || root.TryGetProperty("profiles", out _))
            {
                var pack = JsonSerializer.Deserialize<ProfilePack>(text, JsonOptions);
                return pack.Profiles.Select(Validate).ToList();
            }

            return new List<Profile> { Validate(JsonSerializer.Deserialize<Profile>(text, JsonOptions)) };
        }

        private static Profile Validate(Profile profile)
        {
            if (profile == null)
            {
                throw new InvalidDataException("The file doesn't contain a profile.");
            }
            if (profile.Version > Profile.CurrentVersion)
            {
                throw new InvalidDataException($"Profile '{profile.Name}' was made by a newer version of Better Bravo Lights (format version {profile.Version}).");
            }
            profile.Format = Profile.FormatName;
            profile.Version = Profile.CurrentVersion;
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = "Imported profile";
            }
            profile.Aircraft ??= new();
            profile.Lights ??= new();
            return profile;
        }

        public static Profile ReadProfileFile(string path)
        {
            var text = File.ReadAllText(path);
            return Validate(JsonSerializer.Deserialize<Profile>(text, JsonOptions));
        }

        public static void ExportProfile(Profile profile, string path)
        {
            var copy = profile.Clone();
            copy.Normalise();
            File.WriteAllText(path, JsonSerializer.Serialize(copy, JsonOptions));
        }

        public static void ExportPack(IEnumerable<Profile> profiles, string path)
        {
            var pack = new ProfilePack
            {
                Profiles = profiles.Select(p => { var c = p.Clone(); c.Normalise(); return c; }).ToList()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(pack, JsonOptions));
        }

        /// <summary>
        /// Adds any built-in templates that the user hasn't seen before. Templates the user has deliberately
        /// deleted are recorded in <paramref name="knownTemplateIds"/> and aren't added again.
        /// </summary>
        /// <returns>The number of templates added.</returns>
        public int SeedFromTemplates(TemplateLibrary templates, ISet<string> knownTemplateIds, bool force = false)
        {
            var added = 0;
            foreach (var template in templates.Templates)
            {
                var alreadyHave = Profiles.Any(p => p.Id == template.Id || p.TemplateId == template.Id);
                if (alreadyHave || (!force && knownTemplateIds.Contains(template.Id)))
                {
                    knownTemplateIds.Add(template.Id);
                    continue;
                }

                if (template.IsDefault && DefaultProfile != null)
                {
                    knownTemplateIds.Add(template.Id);
                    continue;
                }

                var copy = template.Clone();
                copy.TemplateId = template.Id;
                copy.FilePath = null;
                SaveQuietly(copy);
                knownTemplateIds.Add(template.Id);
                added++;
            }

            if (added > 0)
            {
                RaiseChanged();
            }
            return added;
        }

        /// <summary>
        /// Ensures there's always a default profile.
        /// </summary>
        public void EnsureDefaultProfile()
        {
            if (DefaultProfile != null)
            {
                return;
            }
            SaveQuietly(new Profile
            {
                Id = Profile.DefaultProfileId,
                Name = "Default (all aircraft)",
                IsDefault = true,
                InheritDefault = false,
                Description = "Used for any aircraft without its own profile."
            });
        }

        private void SaveQuietly(Profile profile)
        {
            var handler = ProfilesChanged;
            ProfilesChanged = null;
            try
            {
                Save(profile);
            }
            finally
            {
                ProfilesChanged = handler;
            }
        }

        private void WriteFile(Profile profile, string path)
        {
            SuppressWatcher();
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }

        private void SuppressWatcher()
        {
            suppressWatcherUntil = DateTime.UtcNow.AddSeconds(1);
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Failed to delete {0}", path);
            }
        }

        public static string MakeFileName(Profile profile)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(profile.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (name.Length == 0)
            {
                name = profile.Id;
            }
            if (name.Length > 80)
            {
                name = name[..80];
            }
            return name + ProfileExtension;
        }

        private static string UniquePath(string path)
        {
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var n = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{stem} ({n++}){ext}");
            } while (File.Exists(candidate));
            return candidate;
        }

        public void Dispose()
        {
            watcher?.Dispose();
            reloadTimer.Dispose();
        }
    }

    /// <summary>
    /// The read-only templates shipped with the program.
    /// </summary>
    public sealed class TemplateLibrary
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public TemplateLibrary(string directory)
        {
            Directory = directory;
        }

        public string Directory { get; }

        public IReadOnlyList<Profile> Templates { get; private set; } = Array.Empty<Profile>();

        public void Load()
        {
            var list = new List<Profile>();
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + ProfileStore.ProfileExtension))
                {
                    try
                    {
                        var p = ProfileStore.ReadProfileFile(file);
                        p.FilePath = file;
                        list.Add(p);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "Failed to read template {0}", file);
                    }
                }
            }
            Templates = list.OrderBy(p => p.IsDefault ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
