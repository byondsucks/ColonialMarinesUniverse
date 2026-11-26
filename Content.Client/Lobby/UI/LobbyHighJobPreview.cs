using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI;

internal sealed class LobbyHighJobPreviewEntry
{
    public LobbyHighJobPreviewEntry(JobPrototype job, IReadOnlyList<string> gamemodeLabels,
        JobPrototype? labelJob = null)
    {
        Job = job;
        GamemodeLabels = gamemodeLabels;
        LabelJob = labelJob;
    }

    public JobPrototype Job { get; }

    public JobPrototype? LabelJob { get; }

    public IReadOnlyList<string> GamemodeLabels { get; }

    public string JobName => LobbyHighJobPreview.GetDisplayJobName(LabelJob ?? Job);

    public string DisplayName
    {
        get
        {
            if (GamemodeLabels.Count == 0)
                return JobName;

            return $"{string.Join("+", GamemodeLabels)} / {JobName}";
        }
    }

    public string Signature => $"{Job.ID}:{string.Join("+", GamemodeLabels)}";
}

internal static class LobbyHighJobPreview
{
    private const string StrandedThirdPartyJobId = "AU14JobStrandedThirdPartyMember";
    private const string SurvivorJobPrefix = "CMUSurvivorJob";

    private static readonly string[] HiddenFactionSuffixes =
    {
        " (GOVFOR)",
        " (OPFOR)"
    };

    private static readonly (string Key, string Label)[] Gamemodes =
    {
        ("ForceOnForce", "FOF"), // CMU14: FoF joins the preview; its rows read the dealing priority below.
        ("Insurgency", "INS"),
        ("DistressSignal", "DS"),
        ("ColonyFall", "CF"),
        // ("Jailbreak", "JB"),
        // ("Prometheus", "PRO"),
        // ("Criminal", "CRI")
    };

    private static IEnumerable<JobPrototype> PreviewJobsFor(JobPrototype job, IPrototypeManager prototypeManager)
    {
        if (job.ID != StrandedThirdPartyJobId)
        {
            yield return job;
            yield break;
        }

        var expanded = false;
        foreach (var survivor in prototypeManager.EnumeratePrototypes<JobPrototype>()
                     .Where(survivor => survivor.ID.StartsWith(SurvivorJobPrefix, StringComparison.Ordinal))
                     .OrderBy(survivor => survivor.ID, StringComparer.Ordinal))
        {
            expanded = true;
            yield return survivor;
        }

        if (!expanded)
            yield return job;
    }

    public static string GetDisplayJobName(JobPrototype job)
    {
        var name = !string.IsNullOrWhiteSpace(job.SpawnMenuRoleName)
            ? (IoCManager.Resolve<ILocalizationManager>().TryGetString(job.SpawnMenuRoleName, out var loc) ? loc : job.SpawnMenuRoleName)
            : job.LocalizedName;
        return TrimHiddenFactionSuffix(name);
    }

    public static List<LobbyHighJobPreviewEntry> GetHighPriorityJobs(
        HumanoidCharacterProfile profile,
        IPrototypeManager prototypeManager)
    {
        var jobOrder = new List<string>();
        var jobs = new Dictionary<string, JobPrototype>();
        var gamemodeLabels = new Dictionary<string, List<string>>();
        var labelJobs = new Dictionary<string, JobPrototype?>();

        void AddHigh(JobPrototype job, string gamemodeLabel, JobPrototype? labelJob = null)
        {
            if (!jobs.ContainsKey(job.ID))
            {
                jobOrder.Add(job.ID);
                jobs[job.ID] = job;
                gamemodeLabels[job.ID] = new List<string>();
                labelJobs[job.ID] = labelJob;
            }

            var labels = gamemodeLabels[job.ID];
            if (!labels.Contains(gamemodeLabel))
                labels.Add(gamemodeLabel);
        }

        foreach (var (gamemode, label) in Gamemodes)
        {
            // CMU14: FoF dealing elevates equivalent roles across sides, so its rows scan every
            // job through the dealing getter; the stored dict alone misses the elevated twins.
            if (gamemode == "ForceOnForce")
            {
                foreach (var job in prototypeManager.EnumeratePrototypes<JobPrototype>())
                {
                    if (!profile.IsForceOnForceJobReachable(job))
                        continue;

                    if (profile.GetForceOnForceJobPriority(job, prototypeManager) != JobPriority.High)
                        continue;

                    foreach (var preview in PreviewJobsFor(job, prototypeManager))
                        AddHigh(preview, label, preview.ID == job.ID ? null : job);
                }

                continue;
            }

            foreach (var (jobId, priority) in profile.GetJobPrioritiesForGamemode(gamemode))
            {
                if (!prototypeManager.TryIndex(jobId, out JobPrototype? job))
                    continue;

                if (priority != JobPriority.High)
                    continue;

                foreach (var preview in PreviewJobsFor(job, prototypeManager))
                    AddHigh(preview, label, preview.ID == job.ID ? null : job);
            }
        }

        var entries = new List<LobbyHighJobPreviewEntry>();
        foreach (var jobId in jobOrder)
        {
            var labels = gamemodeLabels[jobId];

            // A job high-priority in every mode needs no labels - that is the common case, since
            // GetJobPrioritiesForGamemode falls back to the character's general priorities for any
            // mode they haven't customised. Listing all of them would just be noise.
            if (labels.Count == Gamemodes.Length)
                labels = new List<string>();

            entries.Add(new LobbyHighJobPreviewEntry(jobs[jobId], labels, labelJobs.GetValueOrDefault(jobId)));
        }

        // Nothing marked High anywhere still leaves a sprite on screen, so name whatever job the
        // character actually has rather than showing an unexplained figure.
        if (entries.Count == 0 && TryGetFallbackJob(profile, prototypeManager) is { } fallback)
            entries.Add(fallback);

        return entries;
    }

    private static LobbyHighJobPreviewEntry? TryGetFallbackJob(
        HumanoidCharacterProfile profile,
        IPrototypeManager prototypeManager)
    {
        // Passing null gets the character's general priorities rather than any per-mode overrides.
        var priorities = profile.GetJobPrioritiesForGamemode(null);

        foreach (var wanted in new[] { JobPriority.High, JobPriority.Medium, JobPriority.Low })
        {
            foreach (var (jobId, priority) in priorities)
            {
                if (priority != wanted || !prototypeManager.TryIndex(jobId, out JobPrototype? job))
                    continue;

                var preview = PreviewJobsFor(job, prototypeManager).FirstOrDefault();
                if (preview != null)
                {
                    return new LobbyHighJobPreviewEntry(preview, Array.Empty<string>(),
                        preview.ID == job.ID ? null : job);
                }
            }
        }

        return null;
    }

    public static string GetSignature(IReadOnlyList<LobbyHighJobPreviewEntry> entries)
    {
        if (entries.Count == 0)
            return string.Empty;

        return string.Join("|", entries.Select(entry => entry.Signature));
    }

    private static string TrimHiddenFactionSuffix(string name)
    {
        var trimmed = name.TrimEnd();
        foreach (var suffix in HiddenFactionSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(0, trimmed.Length - suffix.Length).TrimEnd();
        }

        return name;
    }
}
