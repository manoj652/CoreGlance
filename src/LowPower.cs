using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CoreGlance
{
    /// <summary>
    /// Low power mode: switches to a copy of the user's own power plan with the CPU capped and tuned for efficiency,
    /// then switches back. Nothing else on the plan changes, and turning it off deletes the copy.
    /// Measured on an i5-1335U at full load: 31-35 W on the stock plan, 8.2 W with Battery saver (36% of the speed).
    /// </summary>
    static class LowPower
    {
        public const string PlanGuid = "c0e61a5c-4c50-4d00-9000-0000000000a1";
        const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
        const string FreqMaxP = "75b0ae3f-bce0-45a7-8c89-c9611c25e100", FreqMaxE = "75b0ae3f-bce0-45a7-8c89-c9611c25e101";
        const string EppP = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", EppE = "36687f9e-e3a5-4dbf-b1dc-15eb381c6864";
        const string Sched = "93b8b6dc-0698-4d1c-9ee4-0644e900c85d", ShortSched = "bae08b81-2d5e-4688-ad6a-13243356654b";
        const string SubEnergySaver = "de830923-a562-41af-a086-e3a2c6bad2da", SaverThreshold = "e69653ca-cf7f-4f05-aa73-cb833fa90ad4";
        const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        public static string LevelName(int level) { return level == 1 ? "Max saver" : "Battery saver"; }
        public static string LevelSummary(int level) { return level == 1 ? "CPU capped at 1.6 GHz, E-cores preferred" : "CPU capped at 2.0 GHz, tuned for efficiency"; }

        static int Powercfg(string args, out string output)
        {
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        static bool Run(string args)
        {
            string o;
            int rc = Powercfg(args, out o);
            if (rc != 0) Settings.Log("powercfg " + args + ": " + rc + " " + o.Trim());
            return rc == 0;
        }

        public static string ActivePlan()
        {
            string o;
            if (Powercfg("/getactivescheme", out o) != 0) return null;
            Match m = Regex.Match(o, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        public static bool IsActive()
        {
            return string.Equals(ActivePlan(), PlanGuid, StringComparison.OrdinalIgnoreCase);
        }

        static bool PlanExists(string guid)
        {
            string o;
            return Powercfg("/list", out o) == 0 && o.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Turns it on. Returns the plan to go back to (the one active before), or null on failure.</summary>
        public static string Enable(int level, string previous)
        {
            string active = ActivePlan();
            if (active == null) return null;
            if (!string.Equals(active, PlanGuid, StringComparison.OrdinalIgnoreCase)) previous = active;
            if (string.IsNullOrEmpty(previous) || !PlanExists(previous)) previous = Balanced;

            // Rebuild the copy from the user's current plan, so their own display and sleep settings carry over.
            if (string.Equals(active, PlanGuid, StringComparison.OrdinalIgnoreCase)) Run("/setactive " + previous);
            string ignore;
            Powercfg("/delete " + PlanGuid, out ignore);
            if (!Run("/duplicatescheme " + previous + " " + PlanGuid)) return null;
            Run("/changename " + PlanGuid + " \"CoreGlance Low Power\" \"Made by CoreGlance. Turn off low power mode in CoreGlance to go back to your normal plan.\"");

            int capP = level == 1 ? 1600 : 2000, capE = level == 1 ? 1300 : 1600, epp = level == 1 ? 90 : 80;
            bool ok = true;
            foreach (string mode in new[] { "/setacvalueindex ", "/setdcvalueindex " })
            {
                ok &= Run(mode + PlanGuid + " " + SubProcessor + " " + FreqMaxP + " " + capP);
                ok &= Run(mode + PlanGuid + " " + SubProcessor + " " + FreqMaxE + " " + capE);
                ok &= Run(mode + PlanGuid + " " + SubProcessor + " " + EppP + " " + epp);
                Run(mode + PlanGuid + " " + SubProcessor + " " + EppE + " " + epp);   // absent on CPUs without E-cores
                if (level == 1)
                {
                    Run(mode + PlanGuid + " " + SubProcessor + " " + Sched + " 4");        // prefer efficient processors
                    Run(mode + PlanGuid + " " + SubProcessor + " " + ShortSched + " 4");
                }
            }
            Run("/setdcvalueindex " + PlanGuid + " " + SubEnergySaver + " " + SaverThreshold + " 100");   // Windows Energy Saver whenever on battery
            if (!ok || !Run("/setactive " + PlanGuid))
            {
                Run("/setactive " + previous);
                Powercfg("/delete " + PlanGuid, out ignore);
                return null;
            }
            return previous;
        }

        static int ReadValue(bool ac, string sub, string setting)
        {
            string o;
            if (Powercfg("/qh " + PlanGuid + " " + sub + " " + setting, out o) != 0) return -1;
            Match m = Regex.Match(o, (ac ? "AC" : "DC") + @" Power Setting Index: 0x([0-9a-fA-F]+)");
            return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
        }

        /// <summary>Diagnostic (CoreGlance.exe --low-power-test out.txt): on, change level, off; checks every step.</summary>
        public static string SelfTest()
        {
            var sb = new System.Text.StringBuilder();
            int fails = 0;
            Action<string, bool> check = delegate(string what, bool ok) { sb.AppendLine((ok ? "ok    " : "FAIL  ") + what); if (!ok) fails++; };
            string before = ActivePlan();
            sb.AppendLine("plan before: " + before);
            string prev = Enable(0, "");
            check("battery saver turns on", prev != null && IsActive());
            check("goes back to the plan that was active", string.Equals(prev, before, StringComparison.OrdinalIgnoreCase));
            check("P-core cap 2000 MHz (AC and DC)", ReadValue(true, SubProcessor, FreqMaxP) == 2000 && ReadValue(false, SubProcessor, FreqMaxP) == 2000);
            check("efficiency preference 80", ReadValue(false, SubProcessor, EppP) == 80);
            check("Energy Saver whenever on battery", ReadValue(false, SubEnergySaver, SaverThreshold) == 100);
            string prev2 = Enable(1, prev);
            check("switching to max saver keeps the original plan to return to", string.Equals(prev2, before, StringComparison.OrdinalIgnoreCase) && IsActive());
            check("P-core cap 1600 MHz", ReadValue(false, SubProcessor, FreqMaxP) == 1600);
            check("prefers efficient cores", ReadValue(false, SubProcessor, Sched) == 4);
            check("turns off", Disable(prev2));
            check("original plan active again", string.Equals(ActivePlan(), before, StringComparison.OrdinalIgnoreCase));
            check("its copy is deleted", !PlanExists(PlanGuid));
            check("turning off twice is harmless", Disable(prev2) && string.Equals(ActivePlan(), before, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
            return sb.ToString();
        }

        /// <summary>Back to the user's plan, and the copy is removed.</summary>
        public static bool Disable(string previous)
        {
            if (string.IsNullOrEmpty(previous) || !PlanExists(previous)) previous = Balanced;
            bool ok = !IsActive() || Run("/setactive " + previous);
            string ignore;
            if (ok) Powercfg("/delete " + PlanGuid, out ignore);
            return ok;
        }
    }
}
