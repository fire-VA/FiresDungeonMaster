using System;
using System.Reflection;
using UnityEngine;

namespace FiresDungeonMaster.UI
{
    /// <summary>
    /// Lock-picking minigame audio: pin-SET clicks (pitch-ramped — each seated pin rings a semitone higher), the DRAG
    /// scrape (looped while raising, intensity-driven), wrong-tap THUD and timer TICK (synthesized, PencilSfx idiom),
    /// the pick-SNAP (randomly pitched so repeats never sound identical), and the door cues (requires-key / unlock /
    /// open). Clips are embedded 16-bit PCM WAVs except the two synthesized ones.
    ///
    /// These are 2D UI sounds, so the sources follow the vanilla contract for 2D audio (ZSFX): bypassReverbZones=true
    /// and routed through AudioMan's GUI mixer group. Without that, a dungeon's reverb zone smears every click into a
    /// distant echo and the player's volume settings are ignored. Driven by REFLECTION on the Unity AudioClip/
    /// AudioSource API — a direct compile-time ref to UnityEngine.AudioModule breaks the net48 build. Client-only.
    /// </summary>
    internal static class LockpickAudio
    {
        private const float ClipGain = 2.0f;       // 2x louder (user) — applied to sample data on load, clamped
        private const float DragGainBoost = 2.0f;  // rattle/scrape ANOTHER 2x on top (user: drowned out by game music)
        private static bool _init, _failed, _routed;
        private static Type _tClip, _tSource;
        private static MethodInfo _clipCreate, _clipSetData, _playOneShot, _play, _stop;
        private static PropertyInfo _pClip, _pLoop, _pSpatial, _pPlayOnAwake, _pIsPlaying, _pVolume, _pPitch, _pOutputGroup, _pBypassReverb;
        private static Component _oneShot, _pitchShot, _loopSrc;
        private static object _pinSet, _drag, _requiresKey, _doorUnlock, _doorOpen, _break1, _break2, _miss, _tick;

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>Pin seated. Each successive pin clicks a semitone higher — the rising "progress" ramp.</summary>
        public static void PlayPinSet(int pinsAlreadySet) { EnsureInit(); ShotPitched(_pinSet, 0.85f, Mathf.Pow(1.059463f, pinsAlreadySet)); }
        /// <summary>Wrong [E] — a dull thud, slightly pitch-varied so repeated fumbles don't sound canned.</summary>
        public static void PlayMiss() { EnsureInit(); ShotPitched(_miss, 0.8f, UnityEngine.Random.Range(0.94f, 1.06f)); }
        /// <summary>One soft clock tick per second while the timer is in the red. Urgency 0..1 raises volume + pitch.</summary>
        public static void PlayTick(float urgency) { EnsureInit(); ShotPitched(_tick, Mathf.Lerp(0.3f, 0.55f, urgency), Mathf.Lerp(1f, 1.15f, urgency)); }
        public static void PlayRequiresKey() { EnsureInit(); Shot(_requiresKey, 0.9f); }
        public static void PlayDoorUnlock() { EnsureInit(); Shot(_doorUnlock, 0.9f); }   // latch click on key/lockpick success
        public static void PlayDoorOpen() { EnsureInit(); Shot(_doorOpen, 0.95f); }       // creak when the door opens (enter)
        /// <summary>Lockpick snaps — one of two clips, randomly pitched so back-to-back breaks differ.</summary>
        public static void PlayBreak() { EnsureInit(); ShotPitched(UnityEngine.Random.value < 0.5f ? _break1 : _break2, 0.95f, UnityEngine.Random.Range(0.88f, 1.12f)); }

        public static void StartDrag()
        {
            EnsureInit();
            if (_failed || _loopSrc == null || _drag == null || _pIsPlaying == null) return;
            EnsureRouted();
            try { if (!(bool)_pIsPlaying.GetValue(_loopSrc)) _play.Invoke(_loopSrc, null); } catch { }
        }

        /// <summary>
        /// Drive the looping scrape by how CLOSE the pin is to its sweet spot (0 = far/cold, 1 = right in it). Louder +
        /// higher-pitched the nearer you get — the audible half of the "hotter as you approach" cue. Ensures the loop
        /// is playing. Client-only; a no-op if audio failed to init.
        /// </summary>
        public static void SetDragIntensity(float t)
        {
            EnsureInit();
            if (_failed || _loopSrc == null || _drag == null || _pIsPlaying == null) return;
            EnsureRouted();
            t = Mathf.Clamp01(t);
            try
            {
                _pVolume?.SetValue(_loopSrc, Mathf.Lerp(0.16f, 1f, t));
                _pPitch?.SetValue(_loopSrc, Mathf.Lerp(0.9f, 1.32f, t));
                if (!(bool)_pIsPlaying.GetValue(_loopSrc)) _play.Invoke(_loopSrc, null);
            }
            catch { }
        }

        public static void StopDrag()
        {
            if (_loopSrc == null || _pIsPlaying == null || _stop == null) return;
            try { if ((bool)_pIsPlaying.GetValue(_loopSrc)) _stop.Invoke(_loopSrc, null); } catch { }
        }

        private static void Shot(object clip, float vol)
        {
            if (_failed || _oneShot == null || clip == null) return;
            EnsureRouted();
            try { _playOneShot.Invoke(_oneShot, new object[] { clip, vol }); } catch { }
        }

        // Pitched one-shots go through their OWN source: setting pitch on the shared source would detune any door cue
        // still ringing on it.
        private static void ShotPitched(object clip, float vol, float pitch)
        {
            if (_failed || _pitchShot == null || clip == null) return;
            EnsureRouted();
            try
            {
                _pPitch?.SetValue(_pitchShot, pitch);
                _playOneShot.Invoke(_pitchShot, new object[] { clip, vol });
            }
            catch { }
        }

        // The vanilla 2D-audio contract (ZSFX): UI sounds bypass world reverb zones — otherwise a crypt's reverb makes
        // every click sound like it plays from far away — and route through AudioMan's GUI mixer so the player's
        // volume settings apply. AudioMan may not be up on the very first call, so retry the mixer until it is.
        private static void EnsureRouted()
        {
            if (_routed || _failed || _oneShot == null) return;
            try
            {
                _pBypassReverb?.SetValue(_oneShot, true);
                _pBypassReverb?.SetValue(_pitchShot, true);
                _pBypassReverb?.SetValue(_loopSrc, true);
                var man = AudioMan.instance;
                object gui = man != null ? typeof(AudioMan).GetField("m_guiMixer")?.GetValue(man) : null;
                if (gui == null) return;
                _pOutputGroup?.SetValue(_oneShot, gui);
                _pOutputGroup?.SetValue(_pitchShot, gui);
                _pOutputGroup?.SetValue(_loopSrc, gui);
                _routed = true;
            }
            catch { _routed = true; }
        }

        private static void EnsureInit()
        {
            if (_init) return;
            _init = true;
            if (IsClientOnly()) { _failed = true; return; }
            try
            {
                _tClip = Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule") ?? Type.GetType("UnityEngine.AudioClip, UnityEngine");
                _tSource = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule") ?? Type.GetType("UnityEngine.AudioSource, UnityEngine");
                if (_tClip == null || _tSource == null) { _failed = true; return; }
                _clipCreate = _tClip.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
                _clipSetData = _tClip.GetMethod("SetData", new[] { typeof(float[]), typeof(int) });
                _playOneShot = _tSource.GetMethod("PlayOneShot", new[] { _tClip, typeof(float) });
                _play = _tSource.GetMethod("Play", Type.EmptyTypes);
                _stop = _tSource.GetMethod("Stop", Type.EmptyTypes);
                _pClip = _tSource.GetProperty("clip");
                _pLoop = _tSource.GetProperty("loop");
                _pSpatial = _tSource.GetProperty("spatialBlend");
                _pPlayOnAwake = _tSource.GetProperty("playOnAwake");
                _pIsPlaying = _tSource.GetProperty("isPlaying");
                _pVolume = _tSource.GetProperty("volume");
                _pPitch = _tSource.GetProperty("pitch");
                _pOutputGroup = _tSource.GetProperty("outputAudioMixerGroup");
                _pBypassReverb = _tSource.GetProperty("bypassReverbZones");
                if (_clipCreate == null || _clipSetData == null || _playOneShot == null || _play == null || _stop == null) { _failed = true; return; }

                _pinSet = LoadClip("FiresLockPinSet.wav", "fdm_pinset");
                _drag = LoadClip("FiresLockDrag.wav", "fdm_drag", DragGainBoost);
                _requiresKey = LoadClip("FiresLockRequiresKey.wav", "fdm_reqkey");
                _doorUnlock = LoadClip("FiresDoorUnlock.wav", "fdm_doorunlock");
                _doorOpen = LoadClip("FiresDoorOpen.wav", "fdm_dooropen");
                _break1 = LoadClip("FiresLockBreak1.wav", "fdm_break1");
                _break2 = LoadClip("FiresLockBreak2.wav", "fdm_break2");
                _miss = MakeThud();
                _tick = MakeTick();

                var go = new GameObject("FDM_LockAudio");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _oneShot = go.AddComponent(_tSource);
                _pPlayOnAwake?.SetValue(_oneShot, false);
                _pSpatial?.SetValue(_oneShot, 0f);
                _pitchShot = go.AddComponent(_tSource);
                _pPlayOnAwake?.SetValue(_pitchShot, false);
                _pSpatial?.SetValue(_pitchShot, 0f);
                _loopSrc = go.AddComponent(_tSource);
                _pPlayOnAwake?.SetValue(_loopSrc, false);
                _pSpatial?.SetValue(_loopSrc, 0f);
                _pLoop?.SetValue(_loopSrc, true);
                if (_drag != null) _pClip?.SetValue(_loopSrc, _drag);
                EnsureRouted();
                FiresLogger.LogInfo($"lock audio ready (pinSet={_pinSet != null} drag={_drag != null} reqKey={_requiresKey != null} routed={_routed}).");
            }
            catch (Exception ex) { _failed = true; FiresLogger.LogWarning($"lock audio init failed: {ex.Message}"); }
        }

        private static object LoadClip(string endsWith, string name, float extraGain = 1f)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = null;
                foreach (var n in asm.GetManifestResourceNames()) if (n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                if (res == null) { FiresLogger.LogError($"lock audio '{endsWith}' not embedded."); return null; }
                byte[] d;
                using (var s = asm.GetManifestResourceStream(res)) using (var ms = new System.IO.MemoryStream()) { s.CopyTo(ms); d = ms.ToArray(); }
                if (!ParseWav(d, extraGain, out float[] buf, out int ch, out int sr)) return null;
                ch = Mathf.Max(1, ch);
                var clip = _clipCreate.Invoke(null, new object[] { name, buf.Length / ch, ch, sr, false });
                _clipSetData.Invoke(clip, new object[] { buf, 0 });
                return clip;
            }
            catch (Exception ex) { FiresLogger.LogError($"lock audio load '{endsWith}': {ex.Message}"); return null; }
        }

        // The wrong-tap THUD and the timer TICK are synthesized (PencilSfx idiom — original, license-clean, nothing to
        // record). The thud is a fast pitch-dropping low sine with a short noise transient; the tick is one short blip.
        private static object MakeThud()
        {
            const int sr = 22050; const float dur = 0.09f;
            int n = (int)(sr * dur);
            var buf = new float[n];
            var rng = new System.Random(90210);
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                float f = 60f + 95f * Mathf.Exp(-t * 34f);
                phase += 2f * Mathf.PI * f / sr;
                float noise = t < 0.006f ? (float)(rng.NextDouble() * 2.0 - 1.0) * 0.5f * (1f - t / 0.006f) : 0f;
                buf[i] = Mathf.Clamp((Mathf.Sin(phase) * 0.9f + noise) * Mathf.Exp(-t / 0.028f), -1f, 1f);
            }
            return MakeMonoClip("fdm_miss", buf, sr);
        }

        private static object MakeTick()
        {
            const int sr = 22050; const float dur = 0.03f;
            int n = (int)(sr * dur);
            var buf = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / sr;
                buf[i] = Mathf.Sin(2f * Mathf.PI * 1150f * t) * Mathf.Exp(-t / 0.005f) * 0.7f;
            }
            return MakeMonoClip("fdm_tick", buf, sr);
        }

        private static object MakeMonoClip(string name, float[] buf, int sampleRate)
        {
            var clip = _clipCreate.Invoke(null, new object[] { name, buf.Length, 1, sampleRate, false });
            _clipSetData.Invoke(clip, new object[] { buf, 0 });
            return clip;
        }

        // Minimal 16-bit PCM WAV parse (bytes) → normalized float samples + format. Mirrors FiresCore.UI.WavLoader.
        private static bool ParseWav(byte[] d, float extraGain, out float[] samples, out int channels, out int sampleRate)
        {
            samples = null; channels = 0; sampleRate = 0;
            if (d == null || d.Length < 44 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return false;
            channels = BitConverter.ToInt16(d, 22);
            sampleRate = BitConverter.ToInt32(d, 24);
            int bits = BitConverter.ToInt16(d, 34);
            if (bits != 16 || channels < 1) return false;
            int p = 12, dataOff = -1, dataLen = 0;
            while (p + 8 <= d.Length)
            {
                int len = BitConverter.ToInt32(d, p + 4);
                if (d[p] == 'd' && d[p + 1] == 'a' && d[p + 2] == 't' && d[p + 3] == 'a') { dataOff = p + 8; dataLen = len; break; }
                p += 8 + len + (len & 1);
            }
            if (dataOff < 0) return false;
            dataLen = Mathf.Min(dataLen, d.Length - dataOff);
            int total = dataLen / 2;
            samples = new float[total];
            // Base 2x gain on all lock/door SFX (user), times any per-clip boost (the drag rattle), clamped.
            float gain = ClipGain * extraGain;
            for (int i = 0; i < total; i++)
                samples[i] = Mathf.Clamp(BitConverter.ToInt16(d, dataOff + i * 2) / 32768f * gain, -1f, 1f);
            return true;
        }
    }
}
