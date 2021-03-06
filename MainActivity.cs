﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Preferences;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.OS;
using UnmanagedOgg;

using Stream = System.IO.Stream;

namespace Falplayer
{
    [Activity (Label = "Falplayer", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ScreenOrientation = ScreenOrientation.Portrait)]
    public class MainActivity : Activity
    {
        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.Main);

            title_database = new TitleDatabase (this);
            player = new Player(title_database, this);
        }
        Player player;
        TitleDatabase title_database;

        internal void CreateSongDirectoryList ()
        {
            var ifs = IsolatedStorageFile.GetUserStoreForApplication();
            var list = GetOggDirectories ("/");
            using (var sw = new StreamWriter(ifs.CreateFile("songdirs.txt")))
                foreach (var dir in list)
                    sw.WriteLine(dir);
        }

        IEnumerable<string> GetOggDirectories (string path)
        {
            foreach (var dir in Directory.EnumerateDirectories (path))
            {
                // FIXME: not sure why, but EnumerateFiles (dir, "*.ogg") fails.
                // FIXME: case insensitive search is desired.
                string [] files;
                try {
                    files = Directory.GetFiles (dir, "*.ogg");
                } catch (UnauthorizedAccessException) {
                    continue;
                }
                if (files.Any ())
                    yield return dir;
                foreach (var sub in GetOggDirectories (dir))
                    yield return sub;
            }
        }
    }

    class PlayerView : Java.Lang.Object, SeekBar.IOnSeekBarChangeListener
    {
        const string from_history_tag = "<from history>";
        Player player;
        TitleDatabase database;
        MainActivity activity;
        Button load_button, play_button, stop_button, rescan_button;
        TextView title_text_view, timeline_text_view;
        SeekBar seekbar;
        long loop_start, loop_length, loop_end, total_length;
        TimeSpan total_time;
        int loops;

        public PlayerView (Player player, TitleDatabase database, MainActivity activity)
        {
            this.player = player;
            this.database = database;
            this.activity = activity;
            this.load_button = activity.FindViewById<Button>(Resource.Id.SelectButton);
            this.play_button = activity.FindViewById<Button>(Resource.Id.PlayButton);
            this.stop_button = activity.FindViewById<Button>(Resource.Id.StopButton);
            this.rescan_button = activity.FindViewById<Button>(Resource.Id.RescanButton);
            this.seekbar = activity.FindViewById<SeekBar>(Resource.Id.SongSeekbar);
            this.title_text_view = activity.FindViewById<TextView>(Resource.Id.SongTitleTextView);
            this.timeline_text_view = activity.FindViewById<TextView>(Resource.Id.TimelineTextView);
            PlayerEnabled = false;

            var ifs = IsolatedStorageFile.GetUserStoreForApplication ();
            if (!ifs.FileExists ("songdirs.txt"))
                load_button.Enabled = false;

            load_button.Click += delegate
            {
                var db = new AlertDialog.Builder (activity);
                db.SetTitle ("Select Music Folder");

                List<string> dirlist = new List<string> ();
                if (ifs.FileExists ("history.txt"))
                    dirlist.Add (from_history_tag);
                using (var sr = new StreamReader (ifs.OpenFile ("songdirs.txt", FileMode.Open)))
                    foreach (var s in sr.ReadToEnd ().Split ('\n'))
                        if (!String.IsNullOrEmpty (s))
                            dirlist.Add (s);
                var dirs = dirlist.ToArray ();

                db.SetItems (dirs, delegate (object o, DialogClickEventArgs e) {
                    string dir = dirs [(int) e.Which];
                    ProcessFileSelectionDialog (dir, delegate (string mus) {
                        player.SelectFile (mus);
                        player.Play ();
                        });
                });
                var dlg = db.Show ();
            };

            play_button.Click += delegate {
                try {
                    if (player.IsPlaying) {
                        player.Pause ();
                    } else {
                        player.Play ();
                    }
                } catch (Exception ex) {
                    play_button.Text = ex.Message;
                }
            };

            stop_button.Click += delegate {
                player.Stop ();
            };

            rescan_button.Click += delegate {
                var db = new AlertDialog.Builder(activity);
                db.SetMessage ("Scan music directories. This operation takes a while.");
                db.SetPositiveButton ("OK", delegate {
                    CreateSongDirectoryList ();
                    load_button.Enabled = true;
                    });
                db.SetCancelable (true);
                db.SetNegativeButton ("Cancel", delegate {});
                db.Show ();
            };
        }

        void CreateSongDirectoryList ()
        {
            var wasPlaying = player.IsPlaying;
            if (wasPlaying)
                player.Pause ();
            load_button.Enabled = false;
            this.title_text_view.Text = "scanning directory...hold on";
            activity.CreateSongDirectoryList ();
            this.title_text_view.Text = "";
            load_button.Enabled = true;
            if (wasPlaying)
                player.Play ();
        }

        internal void SetPlayState ()
        {
            activity.RunOnUiThread (() => play_button.Text = "Pause");
        }

        internal void SetPauseState ()
        {
            activity.RunOnUiThread (() => play_button.Text = "Play");
        }

        void ProcessFileSelectionDialog (string dir, Action<string> action)
        {
            var l = new List<string> ();
            if (dir == from_history_tag) {
                l.AddRange (player.GetPlayHistory ());
            } else {
                if (Directory.Exists (dir))
                    foreach (var file in Directory.GetFiles (dir, "*.ogg"))
                        l.Add (file);
            }
            var db = new AlertDialog.Builder(activity);
            if (l.Count == 0)
                db.SetMessage ("No music files there");
            else {
                db.SetTitle ("Select Music File");
                var files = (from f in l select database.GetTitle (f, (int) new FileInfo (f).Length) ?? Path.GetFileName (f)).ToArray ();
                db.SetItems (files, delegate (object o, DialogClickEventArgs e) {
                    int idx = (int) e.Which;
                    title_text_view.Text = files [idx];
                    action (l [idx]);
                });
            }
            db.Show().Show();
        }

        public void Initialize (long totalLength, long loopStart, long loopLength, long loopEnd, long totalTime)
        {
            loops = 0;
            loop_start = loopStart;
            loop_length = loopLength;
            loop_end = loopEnd;
            total_length = totalLength;
            total_time = TimeSpan.FromMilliseconds (totalTime);
            PlayerEnabled = true;
            Reset ();
        }

        public void Reset ()
        {
            activity.RunOnUiThread (delegate {
                play_button.Text = "Play";
                timeline_text_view.Text = GetTimeline (0, TimeSpan.Zero);
                // Since our AudioTrack bitrate is fake, those markers must be faked too.
                seekbar.Max = (int) total_length;
                seekbar.Progress = 0;
                seekbar.SecondaryProgress = (int) loop_end;
                seekbar.SetOnSeekBarChangeListener (this);
                });
        }

        public bool PlayerEnabled {
            get { return play_button.Enabled; }
            set {
                activity.RunOnUiThread (delegate {
                    play_button.Enabled = value;
                    stop_button.Enabled = value;
                    seekbar.Enabled = value;
                    });
            }
        }

        public void Error (string msgbase, params object[] args)
        {
            activity.RunOnUiThread (delegate {
                PlayerEnabled = false;
                play_button.Text = String.Format(msgbase, args);
                });
        }

        string GetTimeline (long pos, TimeSpan playTime)
        {
            return string.Format ("loop: {0} / cur: {1} / end: {2}\ntime: {3:T} / {4:T}",
                loops, pos, loop_end, playTime, total_time);
        }

        public void ReportProgress (long pos, TimeSpan time)
        {
            activity.RunOnUiThread (delegate {
                timeline_text_view.Text = GetTimeline (pos, time);
                seekbar.Progress = (int) pos;
            });
        }

        public void ProcessLoop (long resetPosition)
        {
            loops++;
            seekbar.Progress = (int)resetPosition;
        }

        public void OnProgressChanged (SeekBar seekBar, int progress, bool fromUser)
        {
            if (!fromUser)
                return;
            player.Seek (progress);
        }

        public void OnStartTrackingTouch (SeekBar seekBar)
        {
            // do nothing
        }

        public void OnStopTrackingTouch (SeekBar seekBar)
        {
            // do nothing
        }
    }

    class Player
    {
        const int CompressionRate = 2;

        Activity activity;
        PlayerView view;
        OggStreamBuffer vorbis_buffer;
        LoopCommentExtension loop;
        CorePlayer task;
        DateTime start_time;

        public Player (TitleDatabase database, MainActivity activity)
        {
            Initialize (database, activity);
        }

        void Initialize (TitleDatabase database, MainActivity activity)
        {
            this.activity = activity;
            view = new PlayerView (this, database, activity);
            task = new CorePlayer (this);
            headset_status_receiver = new HeadphoneStatusReceiver (this);
        }

        internal string[] GetPlayHistory()
        {
            var l = new List<string>();
            var ifs = IsolatedStorageFile.GetUserStoreForApplication ();
            if (ifs.FileExists ("history.txt"))
                using (var sr = new StreamReader(ifs.OpenFile ("history.txt", FileMode.Open)))
                    foreach (var file in sr.ReadToEnd().Split ('\n'))
                        if (!String.IsNullOrEmpty(file))
                            l.Add(file);
            return l.ToArray();
        }

        public void SelectFile (string file)
        {
            var hist = GetPlayHistory ();
            if (!hist.Contains (file)) {
                var ifs = IsolatedStorageFile.GetUserStoreForApplication ();
                using (var sw = new StreamWriter (ifs.OpenFile ("history.txt", FileMode.Create))) {
                    sw.WriteLine(file);
                    foreach (var h in hist.Take (Math.Min (9, hist.Length)))
                        sw.WriteLine (h);
                }
            }

            Stream input = File.OpenRead (file);
            vorbis_buffer = new OggStreamBuffer (input);
            loop = new LoopCommentExtension (vorbis_buffer);
            InitializeVorbisBuffer ();
        }

        public void InitializeVorbisBuffer ()
        {
            view.Initialize (loop.Total * 4, loop.Start * 4, loop.Length * 4, loop.End * 4, vorbis_buffer.GetTotalTime (-1));
            task.LoadVorbisBuffer (vorbis_buffer, loop);
        }

        public LoopCommentExtension Loop {
            get { return loop; }
        }

        public bool IsPlaying
        {
            get { return task.Status == PlayerStatus.Playing; }
        }

        HeadphoneStatusReceiver headset_status_receiver;

        public void Play ()
        {
            if (task.Status == PlayerStatus.Paused)
                task.Resume ();
            else {
                Stop ();
                SpinWait.SpinUntil(() => task.Status == PlayerStatus.Stopped);
                if (task != null)
                    task.Dispose ();
                task = new CorePlayer (this);
                InitializeVorbisBuffer ();
                start_time = DateTime.Now;
                task.Start ();
            }
            view.SetPlayState ();
            activity.RegisterReceiver (headset_status_receiver, new IntentFilter(AudioManager.ActionAudioBecomingNoisy));
        }

        public void Pause ()
        {
            task.Pause ();
            view.SetPauseState ();
        }

        public void Stop ()
        {
            task.Stop ();
        }

        public void Seek (long pos)
        {
            task.Seek (pos);
        }

        internal void OnComplete ()
        {
            view.Reset ();
        }

        internal void OnPlayerError (string msgbase, params object [] args)
        {
            view.Error (msgbase, args);
        }

        internal void OnProgress (long pos)
        {
            view.ReportProgress (pos, DateTime.Now - start_time);
        }

        internal void OnLoop (long resetPosition)
        {
            view.ProcessLoop (resetPosition);
        }

        enum PlayerStatus
        {
            Stopped,
            Playing,
            Paused,
        }

        class CorePlayer : IDisposable
        {
            static readonly int min_buf_size = AudioTrack.GetMinBufferSize(44100 / CompressionRate * 2, (int) ChannelConfiguration.Stereo, Encoding.Pcm16bit);
            int buf_size = min_buf_size * 8;

            AudioTrack audio;
            Player player;
            bool pause, finish;
            AutoResetEvent pause_handle = new AutoResetEvent (false);
            int x;
            byte [] buffer;
            long loop_start, loop_length, loop_end, total;
            Thread player_thread;

            public CorePlayer (Player player)
            {
                this.player = player;
                // "* n" part is adjusted for device.
                audio = new AudioTrack (Android.Media.Stream.Music, 44100 / CompressionRate * 2, ChannelConfiguration.Stereo, Android.Media.Encoding.Pcm16bit, buf_size * 4, AudioTrackMode.Stream);
                // FIXME: when I set to "bufsize / 2 / CompressionRate" with CompressionRate = 2, AudioTrack.Write() blocks for some songs...
                buffer = new byte [buf_size / 4 / CompressionRate];
                player_thread = new Thread (() => DoRun ());
            }

            public PlayerStatus Status { get; private set; }

            public void LoadVorbisBuffer (OggStreamBuffer ovb, LoopCommentExtension loop)
            {
                loop_start = loop.Start * 4;
                loop_length = loop.Length * 4;
                loop_end = loop.End * 4;
                total = loop.Total;
            }

            public void Pause ()
            {
                Status = PlayerStatus.Paused;
                pause = true;
            }

            public void Resume ()
            {
                Status = PlayerStatus.Playing;
                pause = false; // make sure to not get overwritten
                pause_handle.Set ();
            }

            DateTime last_seek;

            public void Seek (long pos)
            {
                if (pos < 0 || pos >= loop_end) 
                    return; // ignore
                if (DateTime.Now - last_seek < TimeSpan.FromMilliseconds (500))
                    return; // too short seek operations
                last_seek = DateTime.Now;
                SpinWait.SpinUntil (() => !pause);
                player.vorbis_buffer.SeekPcm (pos / 4);
                total = pos;
            }

            public void Stop ()
            {
                finish = true; // and let player loop finish.
                pause_handle.Set ();
            }

            public void Start ()
            {
                if (Status != PlayerStatus.Stopped) {
                    Stop ();
                    SpinWait.SpinUntil (() => Status == PlayerStatus.Stopped);
                }
                player_thread.Start ();
            }

            Java.Lang.Object DoRun ()
            {
                player.vorbis_buffer.SeekRaw (0);
                Status = PlayerStatus.Playing;
                x = 0;
                total = 0;

                audio.Play ();
                while (!finish)
                {
                    if (pause) {
                        pause = false;
                        audio.Pause ();
                        pause_handle.WaitOne ();
                        audio.Play ();
                    }
                    long size = player.vorbis_buffer.Read (buffer, 0, buffer.Length);
                    if (size <= 0 || size > buffer.Length) {
                        finish = true;
                        if (size < 0)
                            player.OnPlayerError ("vorbis error : {0}", size);
                        else if (size > buffer.Length)
                            player.OnPlayerError ("buffer overflow : {0}", size);
                        break;
                    }

                    if (size + total >= loop_end)
                        size = loop_end - total; // cut down the buffer after loop
                    total += size;

                    if (++x % 30 == 0)
                        player.OnProgress (total);

                    // downgrade bitrate
                    int actualSize = (int) size * 2 / CompressionRate;
                    for (int i = 1; i < actualSize; i++)
                        buffer [i] = buffer [i * CompressionRate / 2 + (CompressionRate / 2) - 1];
                    if (size > 0) {
                        audio.Flush ();
                        audio.Write (buffer, 0, actualSize);
                    }
                    // loop back to LOOPSTART
                    if (total >= loop_end) {
                        player.vorbis_buffer.SeekPcm (loop_start / 4); // also faked
                        player.OnLoop (loop_start);
                        total = loop_start;
                    }
                }
                audio.Flush ();
                audio.Stop ();
                player.OnComplete ();
                Status = PlayerStatus.Stopped;
                return null;
            }

            public void Dispose ()
            {
                if (audio.PlayState != PlayState.Stopped)
                    audio.Stop ();
                audio.Release ();
            }
        }
    }

    class HeadphoneStatusReceiver : BroadcastReceiver
    {
        Player player;
        public HeadphoneStatusReceiver (Player player)
        {
            this.player = player;
        }

        public override void OnReceive (Context context, Intent intent)
        {
            if (intent.Action == AudioManager.ActionAudioBecomingNoisy)
                player.Pause ();
        }
    }

    public class LoopCommentExtension
    {
        long loop_start = 0, loop_length = int.MaxValue, loop_end = int.MaxValue, total;

        public LoopCommentExtension (OggStreamBuffer owner)
        {
            total = owner.GetTotalPcm (-1);
            foreach (var cmt in owner.GetComment(-1).Comments)
            {
                var comment = cmt.Replace(" ", ""); // trim spaces
                if (comment.StartsWith("LOOPSTART="))
                    loop_start = int.Parse(comment.Substring("LOOPSTART=".Length));
                if (comment.StartsWith("LOOPLENGTH="))
                    loop_length = int.Parse(comment.Substring("LOOPLENGTH=".Length));
            }

            if (loop_start > 0 && loop_length > 0)
                loop_end = (loop_start + loop_length);
        }

        public long Start {
            get { return loop_start; }
        }

        public long Length {
            get { return loop_length; }
        }

        public long End {
            get { return loop_end; }
        }

        public long Total
        {
            get { return total; }
        }
    }

    class TitleDatabase
    {
        public class SongData
        {
            static readonly char [] ws = new char [] {' '};
            public SongData (string line)
            {
                var items = line.Split (ws, StringSplitOptions.RemoveEmptyEntries);
                FileName = items [0];
                FileSize = int.Parse (items [1]);
                Title = line.Substring (items [0].Length + 1 + items [1].Length).Trim ();
            }

            public string FileName { get; set; }
            public int FileSize { get; set; }
            public string Title { get; set; }
        }

        List<SongData> list;

        public TitleDatabase (MainActivity activity)
        {
            list = new List<SongData> ();
            foreach (var ass in activity.Assets.List ("titles")) {
                using (var stream = activity.Assets.Open ("titles/" + ass))
                    foreach (var line in new StreamReader (stream).ReadToEnd ().Replace ("\r", "").Split ('\n'))
                        if (!String.IsNullOrEmpty (line) && !line.StartsWith ("//", StringComparison.Ordinal))
                            list.Add (new SongData (line));
            }
        }

        public string GetTitle (string filename, int fileSize)
        {
            string fn = Path.GetFileName (filename);
            var t = list.FirstOrDefault (i => i.FileName == fn && i.FileSize == fileSize);
            return t != null ? t.Title : null;
        }
    }
}
