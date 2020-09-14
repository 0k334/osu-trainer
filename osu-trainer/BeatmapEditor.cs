﻿//using FsBeatmapProcessor;
using FsBeatmapProcessor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace osu_trainer
{
    internal enum EditorState
    {
        NOT_READY,
        READY,
        GENERATING_BEATMAP
    }

    internal enum BadBeatmapReason
    {
        NO_BEATMAP_LOADED,
        ERROR_LOADING_BEATMAP,
        EMPTY_MAP
    }

    internal class BeatmapEditor
    {
        class ZeroARException
        {
            public ZeroARException()
            {
#if false
                throw new Exception("AR is zero.");
#endif
            }
        }
        private MainForm mainform;
        public BadBeatmapReason NotReadyReason;

        public Beatmap OriginalBeatmap;
        public Beatmap NewBeatmap;

        public decimal StarRating { get; set; }
        public decimal AimRating { get; set; }
        public decimal SpeedRating { get; set; }
        private decimal lockedHP = 0M;
        private decimal lockedCS = 0M;
        private decimal lockedAR = 0M;
        private decimal lockedOD = 0M;
        private int lockedBpm = 200;

        private class ConcurrentRequest
        {
            private static int globalRequestCounter = -1;
            public int RequestNumber { get; set; }

            public ConcurrentRequest()
            {
                RequestNumber = ++globalRequestCounter;
            }
        }

        private class BeatmapRequest : ConcurrentRequest
        {
            public string Name { get; set; }

            public BeatmapRequest(string name) : base()
            {
                Name = name;
            }
        }

        private List<BeatmapRequest> mapChangeRequests = new List<BeatmapRequest>();
        private BeatmapRequest completedBeatmapRequest = null;
        private ConcurrentRequest completedDiffCalcRequest = null;
        private bool serviceBeatmapRequestLocked = false;  // mutex for serviceBeatmapChangeRequest()
        private bool serviceDiffCalcRequestLocked = false;  // mutex for serviceDiffCalcRequest()
        private List<ConcurrentRequest> diffCalcRequests = new List<ConcurrentRequest>();

        // public getters only
        // to set, call set methods
        public bool HpIsLocked { get; private set; } = false;

        public bool CsIsLocked { get; private set; } = false;
        public bool ArIsLocked { get; private set; } = false;
        public bool OdIsLocked { get; private set; } = false;
        public bool BpmIsLocked { get; private set; } = false;
        public bool ScaleAR { get; private set; } = true;
        public bool ScaleOD { get; private set; } = true;
        internal EditorState State { get; private set; }
        public decimal BpmMultiplier { get; set; } = 1.0M;
        public bool NoSpinners { get; private set; }
        public bool EmulateHardrock { get; private set; }
        public bool ChangePitch { get; private set; }

        public BeatmapEditor(MainForm f)
        {
            mainform = f;

            // Load previously saved settings
            NoSpinners = Properties.Settings.Default.NoSpinners;
            ChangePitch = Properties.Settings.Default.ChangePitch;

            SetState(EditorState.NOT_READY);
            NotReadyReason = BadBeatmapReason.NO_BEATMAP_LOADED;
        }

        public event EventHandler StateChanged;

        public event EventHandler BeatmapSwitched;

        public event EventHandler BeatmapModified;

        public event EventHandler ControlsModified;

        public void ForceEventStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        public void ForceEventBeatmapSwitched() => BeatmapSwitched?.Invoke(this, EventArgs.Empty);

        public void ForceEventBeatmapModified() => BeatmapModified?.Invoke(this, EventArgs.Empty);

        public void ForceEventControlsModified() => ControlsModified?.Invoke(this, EventArgs.Empty);

        private Beatmap BeatmapConstructorWrapper(string beatmapFilename)
        {
            Beatmap newBeatmap = new Beatmap(beatmapFilename);
            if (newBeatmap.ApproachRate == -1M)
            {
                newBeatmap.ApproachRate = newBeatmap.OverallDifficulty; // i can't believe this is how old maps used to work...
                if (newBeatmap.ApproachRate == 0)
                {
                    var _ = new ZeroARException();
                }
            }
            return newBeatmap;
        }
        public void GenerateBeatmap()
        {
            if (State != EditorState.READY)
                return;

            SetState(EditorState.GENERATING_BEATMAP);

            bool compensateForDT = (NewBeatmap.ApproachRate > 10 || NewBeatmap.OverallDifficulty > 10);

            // Set metadata
            Beatmap exportBeatmap = new Beatmap(NewBeatmap);
            ModifyBeatmapMetadata(exportBeatmap, BpmMultiplier, ChangePitch, compensateForDT);

            // Slow down map by 1.5x
            if (compensateForDT)
            {
                exportBeatmap.ApproachRate      = DifficultyCalculator.CalculateMultipliedAR(NewBeatmap, 1 / 1.5M);
                if (exportBeatmap.ApproachRate == 0)
                {
                    var _ = new ZeroARException();
                }
                exportBeatmap.OverallDifficulty = DifficultyCalculator.CalculateMultipliedOD(NewBeatmap, 1 / 1.5M);
                decimal compensatedRate = (NewBeatmap.Bpm / OriginalBeatmap.Bpm) / 1.5M;
                exportBeatmap.SetRate(compensatedRate);
            }

            // remove spinners
            if (NoSpinners)
                exportBeatmap.RemoveSpinners();

            // Generate new mp3
            var audioFilePath = Path.Combine(JunUtils.GetBeatmapDirectoryName(OriginalBeatmap), exportBeatmap.AudioFilename);
            var newMp3 = "";
            if (!File.Exists(audioFilePath))
            {
                string inFile = Path.Combine(Path.GetDirectoryName(OriginalBeatmap.Filename), OriginalBeatmap.AudioFilename);
                string outFile = Path.Combine(Path.GetTempPath(), exportBeatmap.AudioFilename);

                SongSpeedChanger.GenerateAudioFile(inFile, outFile, BpmMultiplier, mainform.BackgroundWorker, ChangePitch, compensateForDT);
                newMp3 = outFile;

                // take note of this mp3 in a text file, so we can clean it up later
                string mp3ManifestFile = GetMp3ListFilePath();
                List<string> manifest = File.ReadAllLines(mp3ManifestFile).ToList();
                string beatmapFolder = Path.GetDirectoryName(exportBeatmap.Filename).Replace(Properties.Settings.Default.SongsFolder + "\\", "");
                string mp3RelativePath = Path.Combine(beatmapFolder, exportBeatmap.AudioFilename);
                manifest.Add(mp3RelativePath + " | " + exportBeatmap.Filename);
                File.WriteAllLines(mp3ManifestFile, manifest);
            }
            // save file to temp location (do not directly put into any song folder)
            exportBeatmap.Filename = Path.Combine(Path.GetTempPath(), Path.GetFileName(exportBeatmap.Filename));
            exportBeatmap.Save();

            // create and execute osz
            AddNewBeatmapToSongFolder(Path.GetDirectoryName(OriginalBeatmap.Filename), exportBeatmap.Filename, newMp3);

            // post
            SetState(EditorState.READY);
        }

        private void AddNewBeatmapToSongFolder(string songFolder, string newBeatmapFile, string newMp3)
        {
            // 1. Create osz (just a regular zip file with file ext. renamed to .osz)
            string outputOsz = Path.GetFileNameWithoutExtension(songFolder) + ".osz";
            try
            {
                ZipFile.CreateFromDirectory(songFolder, outputOsz);
            }
            catch (Exception e)
            {
                MessageBox.Show($"Failed to create {outputOsz}... {Environment.NewLine}{e.Message}", "Error");
            }
            // 2. Add new files to zip/osz
            using (ZipArchive archive = ZipFile.Open(outputOsz, ZipArchiveMode.Update))
            {
                archive.CreateEntryFromFile(newBeatmapFile, Path.GetFileName(newBeatmapFile));
                if (newMp3 != "")
                {
                    archive.CreateEntryFromFile(newMp3, Path.GetFileName(newMp3));
                    File.Delete(newMp3);
                }
            }
            // 3. Run the .osz
            Process proc = new Process();
            proc.StartInfo.FileName = outputOsz;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }

        private string MatchGroup(string text, string re, int group)
        {
            foreach (Match m in Regex.Matches(text, re))
                return m.Groups[group].Value;
            return "";
        }

        private string GetMp3ListFilePath()
        {
            string manifest = Path.Combine(Properties.Settings.Default.SongsFolder, "modified_mp3_list.txt");
            if (!File.Exists(manifest))
            {
                FileStream fs = File.Open(manifest, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                fs.Close();
                fs.Dispose();
            }
            return manifest;
        }

        public List<string> GetUnusedMp3s()
        {
            // read manifest file
            List<string> lines = new List<string>();
            string mp3ManifestFile = GetMp3ListFilePath();

            if (!File.Exists(mp3ManifestFile))
                return new List<string>();

            using (var reader = File.OpenText(mp3ManifestFile))
            {
                string line = "";
                while ((line = reader.ReadLine()) != null)
                    lines.Add(line);
            }

            // convert that shit into a dictionary
            var mp3Dict = new Dictionary<string, List<string>>();
            string pattern = @"(.+) \| (.+)";
            string parseMp3(string line) => MatchGroup(line, pattern, 1);
            string parseOsu(string line) => MatchGroup(line, pattern, 2);

            // create dictionary keys
            lines
                .Select(line => parseMp3(line)).ToList()
                .Distinct()
                .ToList()
                .ForEach(mp3 => mp3Dict.Add(mp3, new List<string>()));

            // populate dictionary values
            foreach ((string mp3, string osu) in lines.Select(line => (parseMp3(line), parseOsu(line))))
                mp3Dict[mp3].Add(osu);

            // find all keys where none of the associated beatmaps exist, but the mp3 still exists
            bool noFilesExist(bool acc, string file) => acc && !File.Exists(file);
            return lines
                .Select(line => parseMp3(line))
                .Where(mp3 => mp3Dict[mp3].Aggregate(true, noFilesExist))
                .Where(mp3 => File.Exists(JunUtils.FullPathFromSongsFolder(mp3)))
                .ToList();
        }

        public void CleanUpManifestFile()
        {
            // read file
            string mp3ManifestFile = GetMp3ListFilePath();
            List<string> lines = File.ReadAllText(mp3ManifestFile).Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();
            string pattern = @"(.+) \| (.+)";
            string parseMp3(string line) => MatchGroup(line, pattern, 1);

            // filter out lines whose mp3s no longer exist
            List<string> keepLines = new List<string>();
            foreach (string line in lines)
            {
                var relMp3 = parseMp3(line);
                var absMp3 = JunUtils.FullPathFromSongsFolder(relMp3);
                if (File.Exists(absMp3))
                    keepLines.Add(line);
            }

            // write to file
            File.WriteAllText(mp3ManifestFile, String.Join(Environment.NewLine, keepLines));
        }

        public void RequestBeatmapLoad(string beatmapPath)
        {
            mapChangeRequests.Add(new BeatmapRequest(beatmapPath));

            // acquire mutually exclusive entry into this method
            if (!serviceBeatmapRequestLocked)
                ServiceBeatmapChangeRequest();
            else return; // this method is already being run in another async "thread"
        }

        private async void ServiceBeatmapChangeRequest()
        {
            // acquire mutually exclusive entry into this method
            serviceBeatmapRequestLocked = true;

            Beatmap candidateOriginalBeatmap = null, candidateNewBeatmap = null;
            while (completedBeatmapRequest == null || completedBeatmapRequest.RequestNumber != mapChangeRequests.Last().RequestNumber)
            {
                completedBeatmapRequest = mapChangeRequests.Last();
                candidateOriginalBeatmap = await Task.Run(() => LoadBeatmap(mapChangeRequests.Last().Name));

                if (candidateOriginalBeatmap != null)
                {
                    candidateNewBeatmap = new Beatmap(candidateOriginalBeatmap);
                }

                // if a new request came in, invalidate candidate beatmap and service the new request
            }

            // no new requests, we can commit to using this beatmap
            OriginalBeatmap = candidateOriginalBeatmap;
            NewBeatmap = candidateNewBeatmap;
            if (OriginalBeatmap == null)
            {
                SetState(EditorState.NOT_READY);
                NotReadyReason = BadBeatmapReason.ERROR_LOADING_BEATMAP;
                BeatmapSwitched?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                if (OriginalBeatmap.HitObjectCount == 0)
                {
                    SetState(EditorState.NOT_READY);
                    NotReadyReason = BadBeatmapReason.EMPTY_MAP;
                    BeatmapSwitched?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // Apply multiplier
                    NewBeatmap.SetRate(BpmMultiplier);

                    // Apply bpm scaled settings
                    if (ScaleAR) NewBeatmap.ApproachRate = DifficultyCalculator.CalculateMultipliedAR(candidateOriginalBeatmap, BpmMultiplier);
                    if (NewBeatmap.ApproachRate == 0)
                    {
                        var _ = new ZeroARException();
                    }
                    if (ScaleOD) NewBeatmap.OverallDifficulty = DifficultyCalculator.CalculateMultipliedOD(candidateOriginalBeatmap, BpmMultiplier);

                    // Apply locked settings
                    if (HpIsLocked) NewBeatmap.HPDrainRate = lockedHP;
                    if (CsIsLocked) NewBeatmap.CircleSize = lockedCS;
                    if (ArIsLocked)
                    {
                        NewBeatmap.ApproachRate = lockedAR;
                        if (NewBeatmap.ApproachRate == 0)
                        {
                            var _ = new ZeroARException();
                        }
                    }
                    if (OdIsLocked) NewBeatmap.OverallDifficulty = lockedOD;
                    if (BpmIsLocked) SetBpm(lockedBpm);

                    // Apply Hardrock
                    if (EmulateHardrock) NewBeatmap.CircleSize = OriginalBeatmap.CircleSize * 1.3M;
                    if (EmulateHardrock) NewBeatmap.OverallDifficulty = JunUtils.Clamp(GetScaledOD() * 1.4M, 0M, 10M);

                    SetState(EditorState.READY);
                    RequestDiffCalc();
                    BeatmapSwitched?.Invoke(this, EventArgs.Empty);
                    BeatmapModified?.Invoke(this, EventArgs.Empty);
                }
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
            serviceBeatmapRequestLocked = false;
        }

        public void RequestDiffCalc()
        {
            diffCalcRequests.Add(new ConcurrentRequest());

            // acquire mutually exclusive entry into this method
            if (!serviceDiffCalcRequestLocked)
                ServiceDiffCalcRequest();
            else return; // this method is already being run in another async "thread"
        }

        private async void ServiceDiffCalcRequest()
        {
            // acquire mutually exclusive entry into this method
            serviceDiffCalcRequestLocked = true;

            decimal stars = 0.0M, aim = 0.0M, speed = 0.0M;
            while (completedDiffCalcRequest == null || completedDiffCalcRequest.RequestNumber != diffCalcRequests.Last().RequestNumber)
            {
                completedDiffCalcRequest = diffCalcRequests.Last();
                try
                {
                    (stars, aim, speed) = await Task.Run(() => DifficultyCalculator.CalculateStarRating(NewBeatmap));
                }
                catch (NullReferenceException e)
                {
                    Console.WriteLine(e);
                    Console.WriteLine("lol asdfasdf;lkjasdf");
                }
                // if a new request came in, invalidate the diffcalc result and service the new request
            }
            // we serviced the last request, so we can commit to the diffcalc result
            StarRating = stars;
            AimRating = aim;
            SpeedRating = speed;
            BeatmapModified?.Invoke(this, EventArgs.Empty);

            serviceDiffCalcRequestLocked = false;
        }

        public (decimal, decimal, decimal) GetOriginalBpmData() => GetBpmData(OriginalBeatmap);

        public (decimal, decimal, decimal) GetNewBpmData() => GetBpmData(NewBeatmap);

        private (decimal, decimal, decimal) GetBpmData(Beatmap map) => (map?.Bpm ?? 0, map?.MinBpm ?? 0, map?.MaxBpm ?? 0);

        private void SetState(EditorState s)
        {
            State = s;
            Action action = () => StateChanged?.Invoke(this, EventArgs.Empty);

            if (mainform.InvokeRequired)
                mainform.Invoke(action);
            else
                action.Invoke();
        }

        public void SetHP(decimal value)
        {
            if (State != EditorState.READY)
                return;

            NewBeatmap.HPDrainRate = value;
            lockedHP = value;
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetCS(decimal value)
        {
            if (State != EditorState.READY)
                return;

            EmulateHardrock = false;

            NewBeatmap.CircleSize = value;
            lockedCS = value;
            RequestDiffCalc();
            BeatmapModified?.Invoke(this, EventArgs.Empty);
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetAR(decimal value)
        {
            if (State != EditorState.READY)
                return;

            if (value == 0)
            {
                var _ = new ZeroARException();
            }
            NewBeatmap.ApproachRate = value;
            if (ArIsLocked)
            {
                lockedAR = value;
                if (lockedAR == 0)
                {
                    var _ = new ZeroARException();
                }
            }

            ScaleAR = false;
            BeatmapModified?.Invoke(this, EventArgs.Empty);
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleArLock()
        {
            ArIsLocked = !ArIsLocked;
            if (ArIsLocked)
            {
                ScaleAR = false;
                lockedAR = NewBeatmap.ApproachRate;
            }
            else
            {
                SetScaleAR(true);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetScaleAR(bool value)
        {
            ScaleAR = value;

            if (State == EditorState.NOT_READY)
                return;

            // not applicable for taiko or mania
            if (ScaleAR && NewBeatmap.Mode != GameMode.Taiko && NewBeatmap.Mode != GameMode.Mania)
            {
                if (DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmMultiplier) == 0)
                {
                    var _ = new ZeroARException();
                }
                NewBeatmap.ApproachRate = DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmMultiplier);
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ArIsLocked = false;
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetScaleOD(bool value)
        {
            ScaleOD = value;

            if (State == EditorState.NOT_READY)
                return;

            if (ScaleOD)
            {
                NewBeatmap.OverallDifficulty = DifficultyCalculator.CalculateMultipliedOD(OriginalBeatmap, BpmMultiplier);
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            OdIsLocked = false;
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetOD(decimal value)
        {
            if (State != EditorState.READY)
                return;

            EmulateHardrock = false;

            NewBeatmap.OverallDifficulty = value;
            if (OdIsLocked)
                lockedOD = value;

            ScaleOD = false;
            BeatmapModified?.Invoke(this, EventArgs.Empty);
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleHpLock()
        {
            HpIsLocked = !HpIsLocked;
            if (HpIsLocked)
            {
                lockedHP = NewBeatmap.HPDrainRate;
            }
            else
            {
                NewBeatmap.HPDrainRate = OriginalBeatmap.HPDrainRate;
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleCsLock()
        {
            CsIsLocked = !CsIsLocked;
            EmulateHardrock = false;
            if (CsIsLocked)
            {
                lockedCS = NewBeatmap.CircleSize;
            }
            else
            {
                NewBeatmap.CircleSize = OriginalBeatmap.CircleSize;
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleBpmLock()
        {
            BpmIsLocked = !BpmIsLocked;
            if (BpmIsLocked && NewBeatmap != null)
            {
                lockedBpm = (int)NewBeatmap.Bpm;
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleOdLock()
        {
            OdIsLocked = !OdIsLocked;
            EmulateHardrock = false;
            if (OdIsLocked)
            {
                ScaleOD = false;
                lockedOD = NewBeatmap.OverallDifficulty;
            }
            else
            {
                SetScaleOD(true);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetBpmMultiplier(decimal multiplier)
        {
            if (BpmIsLocked)
            {
                int bpm = (int)(OriginalBeatmap.Bpm * multiplier);
                lockedBpm = bpm;
            }
            ApplyBpmMultiplier(multiplier);
        }

        public void SetBpm(int bpm)
        {
            if (BpmIsLocked)
                lockedBpm = bpm;

            decimal originalBpm = GetOriginalBpmData().Item1;
            if (originalBpm == 0)
                return;

            decimal newMultiplier = bpm / originalBpm;
            newMultiplier = JunUtils.Clamp(newMultiplier, 0.1M, 5.0M);
            ApplyBpmMultiplier(newMultiplier);
        }

        private void ApplyBpmMultiplier(decimal multiplier)
        {
            if (BpmMultiplier == multiplier)
                return;
            if (multiplier < 0.1M)
                BeatmapModified?.Invoke(this, EventArgs.Empty); // reject this value and revert view

            BpmMultiplier = multiplier;

            // make no changes
            if (State == EditorState.NOT_READY)
                return;

            // scale AR (not applicable for taiko or mania)
            if (ScaleAR && !ArIsLocked && NewBeatmap.Mode != GameMode.Taiko && NewBeatmap.Mode != GameMode.Mania)
                NewBeatmap.ApproachRate = DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmMultiplier);

            // scale OD
            if (ScaleOD && !OdIsLocked)
            {
                NewBeatmap.OverallDifficulty = GetScaledOD();
                if (EmulateHardrock)
                    NewBeatmap.OverallDifficulty = JunUtils.Clamp(GetScaledOD() * 1.4M, 0M, 10M);
            }

            // modify beatmap timing
            NewBeatmap.SetRate(BpmMultiplier);

            RequestDiffCalc();
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleChangePitchSetting()
        {
            ChangePitch = !ChangePitch;
            Properties.Settings.Default.ChangePitch = ChangePitch;
            Properties.Settings.Default.Save();
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleNoSpinners()
        {
            NoSpinners = !NoSpinners;
            Properties.Settings.Default.NoSpinners = NoSpinners;
            Properties.Settings.Default.Save();
            ControlsModified?.Invoke(this, EventArgs.Empty);
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }
        internal void ToggleHrEmulation()
        {
            EmulateHardrock = !EmulateHardrock;
            CsIsLocked = false;
            OdIsLocked = false;
            if (EmulateHardrock)
            {
                NewBeatmap.CircleSize = OriginalBeatmap.CircleSize * 1.3M;
                NewBeatmap.OverallDifficulty = JunUtils.Clamp(GetScaledOD() * 1.4M, 0M, 10M);
            }
            else
            {
                NewBeatmap.CircleSize = OriginalBeatmap.CircleSize;
                NewBeatmap.OverallDifficulty = GetScaledOD();
            }
            RequestDiffCalc();
            ControlsModified?.Invoke(this, EventArgs.Empty);
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

        public GameMode? GetMode()
        {
            return OriginalBeatmap?.Mode;
        }

        public decimal GetScaledAR() => DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmMultiplier);

        public decimal GetScaledOD() => DifficultyCalculator.CalculateMultipliedOD(OriginalBeatmap, BpmMultiplier);

        public bool NewMapIsDifferent()
        {
            return (
                NewBeatmap.HPDrainRate != OriginalBeatmap.HPDrainRate ||
                NewBeatmap.CircleSize != OriginalBeatmap.CircleSize ||
                NewBeatmap.ApproachRate != OriginalBeatmap.ApproachRate ||
                NewBeatmap.OverallDifficulty != OriginalBeatmap.OverallDifficulty ||
                Math.Abs(BpmMultiplier - 1.0M) > 0.001M ||
                NoSpinners
            );
        }

        // return the new beatmap object if success
        // return null on failure
        private Beatmap LoadBeatmap(string beatmapPath)
        {
            // test if the beatmap is valid before committing to using it
            Beatmap retMap;
            try
            {
                retMap = BeatmapConstructorWrapper(beatmapPath);
            }
            catch
            {
                Console.WriteLine("Bad .osu file format");
                OriginalBeatmap = null;
                NewBeatmap = null;
                return null;
            }
            // Check if beatmap was loaded successfully
            if (!retMap.Valid || retMap.Filename == null || retMap.Title == null)
            {
                Console.WriteLine("Bad .osu file format");
                return null;
            }

            // Check if this map was generated by osu-trainer
            if (retMap.Tags.Contains("osutrainer"))
            {
                // Try to find original unmodified version
                foreach (string diff in Directory.GetFiles(Path.GetDirectoryName(retMap.Filename), "*.osu"))
                {
                    Beatmap map = BeatmapConstructorWrapper(diff);
                    if (!map.Tags.Contains("osutrainer") && map.BeatmapID == retMap.BeatmapID)
                    {
                        retMap = map;
                        break;
                    }
                }
            }

            return retMap;
        }

        // OUT: beatmap.Version
        // OUT: beatmap.Filename
        // OUT: beatmap.AudioFilename
        // OUT: beatmap.Tags
        private void ModifyBeatmapMetadata(Beatmap map, decimal multiplier, bool changePitch = false, bool preDT = false)
        {
            // Difficulty Name and AudioFilename
            if (preDT)
            {
                string bpm = map.Bpm.ToString("0");
                map.Version += $" {multiplier:0.##}x ({bpm}bpm)";
                map.AudioFilename = $"{Path.GetFileNameWithoutExtension(map.AudioFilename)} {multiplier:0.000}x withDT";
                if (changePitch && Math.Abs(multiplier - 1M) > 0.001M)
                    map.AudioFilename += $" (pitch {(multiplier < 1 ? "lowered" : "raised")})";
                map.AudioFilename += ".mp3";
            }
            else if (Math.Abs(multiplier - 1M) > 0.001M)
            {
                string bpm = map.Bpm.ToString("0");
                map.Version += $" {multiplier:0.##}x ({bpm}bpm)";
                map.AudioFilename = $"{Path.GetFileNameWithoutExtension(map.AudioFilename)} {multiplier:0.000}x";
                if (changePitch)
                    map.AudioFilename += $" (pitch {(multiplier < 1 ? "lowered" : "raised")})";
                map.AudioFilename += ".mp3";
            }

            // Difficulty Name - Difficulty Settings
            string HPCSAROD = "";
            if (NewBeatmap.HPDrainRate != OriginalBeatmap.HPDrainRate)
                HPCSAROD += $" HP{NewBeatmap.HPDrainRate:0.#}";

            if (NewBeatmap.CircleSize != OriginalBeatmap.CircleSize)
                HPCSAROD += $" CS{NewBeatmap.CircleSize:0.#}";

            if (NewBeatmap.ApproachRate != GetScaledAR() || NewBeatmap.ApproachRate > 10M)
                HPCSAROD += $" AR{NewBeatmap.ApproachRate:0.#}";

            if (NewBeatmap.OverallDifficulty != GetScaledOD() || NewBeatmap.OverallDifficulty > 10M)
                HPCSAROD += $" OD{NewBeatmap.OverallDifficulty:0.#}";

            map.Version += HPCSAROD;

            //if (NoSpinners)
            //    map.Version += " nospin";

            // Beatmap File Name
            string artist  = JunUtils.NormalizeText(map.Artist);
            string title   = JunUtils.NormalizeText(map.Title);
            string creator = JunUtils.NormalizeText(map.Creator);
            string diff    = JunUtils.NormalizeText(map.Version);
            map.Filename   = Path.GetDirectoryName(map.Filename) + $"\\{artist} - {title} ({creator}) [{diff}].osu";

            // make this map searchable in the in-game menus
            var TagsWithOsutrainer = map.Tags;
            TagsWithOsutrainer.Add("osutrainer");
            map.Tags = TagsWithOsutrainer; // need to assign like this because Tags is an immutable list
        }

        // dominant, min, max

        public void ResetBeatmap()
        {
            if (State != EditorState.READY)
                return;
            NewBeatmap.HPDrainRate = OriginalBeatmap.HPDrainRate;
            NewBeatmap.CircleSize = OriginalBeatmap.CircleSize;
            if (OriginalBeatmap.ApproachRate == 0)
            {
                var _ = new ZeroARException();
            }
            NewBeatmap.ApproachRate = OriginalBeatmap.ApproachRate;
            NewBeatmap.OverallDifficulty = OriginalBeatmap.OverallDifficulty;
            HpIsLocked = false;
            CsIsLocked = false;
            ArIsLocked = false;
            OdIsLocked = false;
            EmulateHardrock = false;
            ScaleAR = true;
            ScaleOD = true;
            BpmMultiplier = 1.0M;
            NewBeatmap.SetRate(1.0M);
            RequestDiffCalc();
            ControlsModified?.Invoke(this, EventArgs.Empty);
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

    }
}