namespace Ddj200;

// Pure input-policy checks: no MIDI device, transport, app or user draft access.
public static class AnalogSurfaceTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        static MidiMessage Note(int deck, int note, bool down) => new((byte)(0x90 + deck), (byte)note, (byte)(down ? 127 : 0));
        static MidiMessage Jog(int deck, int value, int cc = 0x21) => new((byte)(0xB0 + deck), (byte)cc, (byte)value);
        static bool Key(IReadOnlyList<AnalogIntent> output, string key, int act) =>
            output.Count == 1 && output[0].Method == "v.oai.hid" && output[0].Key == key && output[0].Act == act;
        static bool Rad(IReadOnlyList<AnalogIntent> output, string target, double angle, double distance) =>
            output.Count == 1 && output[0].Method == "v.oai.rad" && output[0].Target == target &&
            output[0].Angle == angle && output[0].Distance == distance;
        static IReadOnlyList<AnalogIntent> Tempo(AnalogSurfaceLogic logic, int value, long now, int deck = 1)
        {
            var first = logic.Input(new((byte)(0xB0 + deck), 0, (byte)(value >> 7)), now);
            var second = logic.Input(new((byte)(0xB0 + deck), 32, (byte)(value & 127)), now + 1);
            return first.Concat(second).ToArray();
        }

        var touch = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        Check(Key(touch.Input(Note(0, 0x36, true), 0), "ENC_PRESS", 1) && touch.IsTouched(0) && !touch.IsTouched(1), "left top starts one encoder hold and only touches left deck");
        Check(touch.Input(Note(0, 0x36, true), 20).Count == 0, "duplicate touch down cannot repeat encoder press");
        foreach (int cc in new[] { 0x21, 0x22, 0x23, 0x29 })
        {
            var discarded = touch.Input(Jog(0, 65, cc), 30 + cc);
            Check(discarded.Count == 1 && discarded[0].Method == "discard" && discarded[0].Reason == "jog_touched", "touched left discards jog family " + cc.ToString("X2"));
        }
        Check(Rad(touch.Input(Jog(1, 65), 100), "joystick.right", 0, 1), "left touch does not suppress right rim");
        Check(!touch.Tick(200).Any(x => x.Key == "ENC_PRESS" && x.Act == 0), "held top does not synthesize release on timer");
        Check(Key(touch.Input(Note(0, 0x36, false), 220), "ENC_PRESS", 0) && !touch.IsTouched(0), "left release ends the original hold");
        Check(!touch.Tick(1000).Any(x => x.Key is "ENC_CW" or "ENC_CC"), "suppressed rotation is never replayed after release");
        Check(Key(touch.Input(Jog(0, 65), 1100), "ENC_CW", 2), "fresh rim rotation works after touch release");

        var rightTouch = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        Check(rightTouch.Input(Note(1, 0x36, true), 0).Count == 0 && rightTouch.IsTouched(1), "right top suppresses without encoder action");
        foreach (int cc in new[] { 0x21, 0x22, 0x23, 0x29 })
        {
            var discarded = rightTouch.Input(Jog(1, 63, cc), 10 + cc);
            Check(discarded.Count == 1 && discarded[0].Method == "discard" && discarded[0].Reason == "jog_touched", "touched right discards jog family " + cc.ToString("X2"));
        }
        Check(Key(rightTouch.Input(Jog(0, 63), 100), "ENC_CC", 2), "right touch does not suppress left rim");
        Check(rightTouch.Input(Note(1, 0x36, false), 200).Count == 0 && !rightTouch.IsTouched(1), "right touch release emits no command");
        Check(rightTouch.Tick(1000).Count == 0, "right touched rotation leaves no delayed burst");
        var shifted = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        shifted.Input(Note(0, 0x67, true), 0);
        var shiftedDiscard = shifted.Input(Jog(0, 65), 10);
        Check(shifted.IsTouched(0) && shiftedDiscard.Count == 1 && shiftedDiscard[0].Method == "discard" && shiftedDiscard[0].Reason == "jog_touched", "shifted top note also blocks its deck");
        shifted.Input(new(0x80, 0x67, 64), 20);
        Check(!shifted.IsTouched(0), "note-off velocity is interpreted as release for shifted touch");

        var rate = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        Check(Key(rate.Input(Jog(0, 65), 0), "ENC_CW", 2), "first left positive tick emits clockwise");
        var limited = rate.Input(Jog(0, 63), 49);
        Check(!limited.Any(x => x.Method == "v.oai.hid"), "left rim rate limit drops opposite tick at 49 ms");
        Check(rate.Tick(1000).Count == 0, "rate-limited rotation has no timer backlog");
        Check(Key(rate.Input(Jog(0, 63), 1000), "ENC_CC", 2), "new negative tick emits counterclockwise after quiet");
        Check(Key(rate.Input(Jog(0, 65), 1050), "ENC_CW", 2), "exact 50 ms boundary accepts new tick");
        Check(!rate.Input(Jog(0, 64), 1200).Any(x => x.Method == "v.oai.hid"), "zero relative delta causes no encoder action");

        var burst = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        Check(Rad(burst.Input(Jog(1, 65), 0), "joystick.right", 0, 1), "right positive rotation enters right direction");
        Check(burst.Tick(179).Count == 0, "right burst remains held before quiet deadline");
        Check(Rad(burst.Tick(180), "joystick.neutral", 0, 0), "right burst returns neutral at 180 ms quiet");
        Check(burst.Tick(500).Count == 0, "right neutral is sent once");
        Check(Rad(burst.Input(Jog(1, 63), 600), "joystick.left", 0.5, 1), "right negative rotation enters left direction");

        var tempo = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        Check(!tempo.TempoCentered && Tempo(tempo, 12000, 0).All(x=>x.Method=="discard"), "tempo starts gated even if first sample is far from center");
        Check(Tempo(tempo, 8192 + 129, 10).All(x=>x.Method=="discard") && !tempo.TempoCentered, "tempo must first enter exact initial center tolerance");
        Check(Tempo(tempo, 8192 + 128, 20).Count == 0 && tempo.TempoCentered, "tempo center tolerance includes 128 boundary");
        Check(Tempo(tempo, 8192 + 511, 30).Count == 0, "tempo deviation below entry threshold remains neutral");
        Check(Rad(Tempo(tempo, 8192 + 512, 40), "joystick.down", 0.25, 1), "tempo positive entry at 512 selects down");
        Check(Tempo(tempo, 14000, 50).Count == 0 && tempo.Tick(10000).Count == 0, "tempo excursion does not repeat while held or on timer");
        Check(Tempo(tempo, 8192 + 257, 10010).Count == 0, "tempo hysteresis holds direction above exit threshold");
        Check(Rad(Tempo(tempo, 8192 + 256, 10020), "joystick.neutral", 0, 0), "tempo exit threshold 256 sends one neutral");
        Check(Tempo(tempo, 8192, 10030).Count == 0, "center repeats do not repeat neutral");
        Check(Rad(Tempo(tempo, 8192 - 512, 10040), "joystick.up", 0.75, 1), "tempo negative excursion selects up");
        Check(Tempo(tempo, 0, 10050).Count == 0, "up excursion likewise emits once");

        var invalid = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        Check(invalid.Input(new(0xB1, 32, 0), 0).Count == 0 && !invalid.TempoCentered, "orphan tempo LSB cannot calibrate center");
        invalid.Input(new(0xB1, 0, 64), 0);
        Check(invalid.Input(new(0xB1, 32, 0), 101).Count == 0 && !invalid.TempoCentered, "stale tempo pair cannot calibrate center");
        Check(Tempo(invalid, 8192, 200, 0).Count == 0 && !invalid.TempoCentered, "left tempo does not calibrate or control right mapping");

        var stop = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        stop.Input(Note(0, 0x36, true), 0);
        var cleanup = stop.Stop();
        Check(cleanup.Any(x => x.Key == "ENC_PRESS" && x.Act == 0) && !stop.IsTouched(0), "stop releases an encoder hold and clears touch state");
        Check(stop.Input(Jog(0, 65), 200).Count == 0 && stop.Tick(500).Count == 0, "stopped analog policy emits no new input or delayed motion");

        var scaled = new AnalogSurfaceLogic(new JogSensitivity(.1, .1));
        for (int i = 0; i < 9; i++)
            Check(scaled.Input(Jog(0, 127), i * 100).Count == 0 && scaled.Input(Jog(1, 65), i * 100).Count == 0, "explicit .1 scale keeps both rims silent for increment " + (i + 1));
        Check(Key(scaled.Input(Jog(0, 65), 900), "ENC_CW", 2), "tenth left increment emits once regardless of MIDI magnitude");
        Check(Rad(scaled.Input(Jog(1, 65), 900), "joystick.right", 0, 1), "tenth right increment starts one burst");
        for (int i = 0; i < 10; i++)
            Check(scaled.Input(Jog(1, 65), 910 + i * 10).Count == 0, "continuing right rotation adds no action during fixed burst " + (i + 1));
        Check(scaled.Tick(1079).Count == 0 && Rad(scaled.Tick(1080), "joystick.neutral", 0, 0), "scaled right burst expires 180 ms after action despite prolonged rotation");
        Check(scaled.Tick(2000).Count == 0, "scaled consumed motion cannot replay after neutral");

        var inverse = new AnalogSurfaceLogic(new JogSensitivity(.1, .1));
        for (int i = 0; i < 9; i++) inverse.Input(Jog(0, 65), i * 100);
        for (int i = 0; i < 9; i++)
            Check(inverse.Input(Jog(0, 63), 900 + i * 100).Count == 0, "direction reversal starts fresh count " + (i + 1));
        Check(Key(inverse.Input(Jog(0, 63), 1800), "ENC_CC", 2), "ten reversed increments emit only new direction");

        var quiet = new AnalogSurfaceLogic(new JogSensitivity(.1, .1));
        for (int i = 0; i < 9; i++) quiet.Input(Jog(0, 65), i * 100);
        Check(quiet.Tick(980).Count == 0 && quiet.Tick(10000).Count == 0, "left fractional motion never emits on quiet timer");
        Check(Key(quiet.Input(Jog(0, 65), 11000), "ENC_CW", 2), "left fraction survives quiet until a fresh physical increment");

        var isolated = new AnalogSurfaceLogic(new JogSensitivity(.1, .1));
        for (int i = 0; i < 9; i++) { isolated.Input(Jog(0, 65), i * 100); isolated.Input(Jog(1, 63), i * 100); }
        isolated.Input(Note(0, 0x36, true), 900);
        Check(Rad(isolated.Input(Jog(1, 63), 950), "joystick.left", .5, 1), "touch clears only same deck fractional motion");
        isolated.Input(Note(0, 0x36, false), 1000);
        bool afterTouchSilent = true;
        for (int i = 0; i < 9; i++) afterTouchSilent &= isolated.Input(Jog(0, 65), 1100 + i * 100).Count == 0;
        Check(afterTouchSilent && Key(isolated.Input(Jog(0, 65), 2000), "ENC_CW", 2), "touch and release require ten fresh increments on touched deck");

        var fastOld = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        var fastNew = new AnalogSurfaceLogic(new JogSensitivity(.1, .1));
        int fastOldActions = 0, fastNewActions = 0;
        for (int i = 0; i < 50; i++)
        {
            fastOldActions += fastOld.Input(Jog(0, 65), i).Count(x => x.Method == "v.oai.hid");
            fastNewActions += fastNew.Input(Jog(0, 65), i).Count(x => x.Method == "v.oai.hid");
        }
        Check(fastOldActions == 1 && fastNewActions == 0, "50 increments at 1 ms produce unscaled 1 versus scaled 0 actions");
        Check(fastNew.Tick(100).Count == 0, "rate rejected inputs cannot create a timer backlog");
        bool freshSilent = true;
        for (int i = 1; i < 9; i++) freshSilent &= fastNew.Input(Jog(0, 65), i * 100).Count == 0;
        Check(freshSilent && Key(fastNew.Input(Jog(0, 65), 900), "ENC_CW", 2), "49 rate rejected inputs add no fractional motion");
        var regularOld = new AnalogSurfaceLogic(new JogSensitivity(1, 1));
        var regularNew = new AnalogSurfaceLogic(new JogSensitivity(.1, .1));
        int oldActions = 0, newActions = 0;
        for (int i = 0; i < 100; i++)
        {
            oldActions += regularOld.Input(Jog(0, 65), i * 100).Count(x => x.Method == "v.oai.hid");
            newActions += regularNew.Input(Jog(0, 65), i * 100).Count(x => x.Method == "v.oai.hid");
        }
        Check(oldActions == 100 && newActions == 10, "100 increments at 100 ms produce old 100 versus scaled 10 actions");
        var oneRightBurst = new AnalogSurfaceLogic();
        for (int i = 0; i < 9; i++) oneRightBurst.Input(Jog(1, 65), i * 10);
        Check(Rad(oneRightBurst.Input(Jog(1, 65), 90), "joystick.right", 0, 1), "continuous right burst starts once after ten increments");
        for (int t = 100; t <= 260; t += 10) oneRightBurst.Input(Jog(1, 65), t);
        Check(Rad(oneRightBurst.Tick(270), "joystick.neutral", 0, 0), "continuous right burst still neutralizes at fixed deadline");
        bool continuedSilent = true;
        for (int t = 270; t <= 440; t += 10) continuedSilent &= oneRightBurst.Input(Jog(1, 65), t).Count == 0;
        Check(continuedSilent, "continuing physical right burst cannot retrigger after neutral");
        Check(oneRightBurst.Tick(620).Count == 0, "right quiet reset adds no pending action");
        bool nextBurstSilent = true;
        for (int i = 0; i < 9; i++) nextBurstSilent &= oneRightBurst.Input(Jog(1, 65), 620 + i * 10).Count == 0;
        Check(nextBurstSilent && Rad(oneRightBurst.Input(Jog(1, 65), 710), "joystick.right", 0, 1), "new right burst after quiet requires ten fresh increments");
        Check(new JogSensitivity().LeftScale == .25 && new JogSensitivity().RightScale == .1, "default tuning changes only left scale to .25");
        foreach (int spacing in new[] { 100, 500, 2000 })
        {
            var slow = new AnalogSurfaceLogic();
            bool firstThreeSilent = true;
            for (int i = 0; i < 3; i++)
            {
                firstThreeSilent &= slow.Input(Jog(0, 65), i * spacing).Count == 0;
                firstThreeSilent &= slow.Tick(i * spacing + spacing - 1).Count == 0;
            }
            Check(firstThreeSilent && Key(slow.Input(Jog(0, 65), 3 * spacing), "ENC_CW", 2), "left default emits on fourth increment with spacing " + spacing + " ms");
            var hundred = new AnalogSurfaceLogic();
            int actions = 0;
            bool timerSilent = true;
            for (int i = 0; i < 100; i++)
            {
                actions += hundred.Input(Jog(0, 65), i * spacing).Count(x => x.Key == "ENC_CW" && x.Act == 2);
                timerSilent &= hundred.Tick(i * spacing + spacing - 1).Count == 0;
            }
            Check(actions == 25 && timerSilent, "100 physical increments produce 25 actions and no timer replay at spacing " + spacing + " ms");
        }
        var defaultInverse = new AnalogSurfaceLogic();
        for (int i = 0; i < 3; i++) defaultInverse.Input(Jog(0, 65), i * 500);
        bool reversedSilent = true;
        for (int i = 0; i < 3; i++) reversedSilent &= defaultInverse.Input(Jog(0, 63), 1500 + i * 500).Count == 0;
        Check(reversedSilent && Key(defaultInverse.Input(Jog(0, 63), 3000), "ENC_CC", 2), "default left reversal discards old fraction and requires four new increments");
        var defaultTouch = new AnalogSurfaceLogic();
        for (int i = 0; i < 3; i++) defaultTouch.Input(Jog(0, 65), i * 500);
        defaultTouch.Input(Note(0, 0x36, true), 1500);
        defaultTouch.Input(Note(0, 0x36, false), 1600);
        bool touchedSilent = true;
        for (int i = 0; i < 3; i++) touchedSilent &= defaultTouch.Input(Jog(0, 65), 2000 + i * 500).Count == 0;
        Check(touchedSilent && Key(defaultTouch.Input(Jog(0, 65), 3500), "ENC_CW", 2), "default left touch discards retained fraction");
        var defaultStop = new AnalogSurfaceLogic();
        for (int i = 0; i < 3; i++) defaultStop.Input(Jog(0, 65), i * 500);
        Check(defaultStop.Stop().Count == 0 && defaultStop.Tick(10000).Count == 0 && defaultStop.Input(Jog(0, 65), 11000).Count == 0, "stop discards left fractional motion without executing it");
        var halfScale = new AnalogSurfaceLogic(new JogSensitivity(.5, .1));
        Check(halfScale.Input(Jog(0, 65), 0).Count == 0 && halfScale.Tick(1999).Count == 0 &&
            Key(halfScale.Input(Jog(0, 65), 2000), "ENC_CW", 2), "half-scale left emits on second slow increment without timer replay");
        var fortyScale = new AnalogSurfaceLogic(new JogSensitivity(.4, .1));
        int fortyActions = 0; bool fortyTimerSilent = true;
        for(int i=0;i<5;i++)
        {
            fortyActions += fortyScale.Input(Jog(0,65), i*2000).Count(x=>x.Key=="ENC_CW" && x.Act==2);
            fortyTimerSilent &= fortyScale.Tick(i*2000+1999).Count==0;
        }
        Check(fortyActions==2 && fortyTimerSilent, "0.4 left produces two actions per five slow increments with no timer replay");
        Console.WriteLine($"{passed} analog surface checks passed; hardware unopened; no Codex actions");
        return 0;
    }
}
