using System;
using System.Collections.Generic;
using System.IO;
using Fm4Zip;

static class SelfTest
{
    static int fails = 0;
    static void Check(bool ok, string msg) { Console.WriteLine((ok ? "OK   " : "FAIL ") + msg); if (!ok) fails++; }

    static int Main(string[] args)
    {
        XMem.Init();
        Check(XMem.Available, "XMem caricata " + XMem.Error);
        if (!XMem.Available) return 1;
        string src = args[0], work = args[1];
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "t.zip");
        File.Copy(src, zip, true);

        ZArchive a = ZArchive.Open(zip);
        Console.WriteLine("entries: " + a.Entries.Count);
        int bad = 0;
        foreach (ZEntry e in a.Entries) { string r = a.Test(e); if (r != null) { bad++; Console.WriteLine("  " + e.Name + ": " + r); } }
        Check(bad == 0, "Test CRC di tutte le entry originali (" + bad + " errori)");

        // 1) identical rewrite => byte-identical file
        List<PlanItem> plan = new List<PlanItem>();
        foreach (ZEntry e in a.Entries) { PlanItem p = new PlanItem(); p.Src = e; plan.Add(p); }
        a.Rewrite(plan, null);
        byte[] o = File.ReadAllBytes(src), n = File.ReadAllBytes(zip);
        bool same = o.Length == n.Length;
        if (same) for (int i = 0; i < o.Length; i++) if (o[i] != n[i]) { same = false; Console.WriteLine("  diff @" + i); break; }
        Check(same, "riscrittura identica = file identico (" + o.Length + " vs " + n.Length + ")");

        // 2) recompress one entry: XMem round trip of original data
        ZEntry big = a.Entries[0];
        byte[] raw = a.Read(big);
        byte[] c = XMem.Compress(raw);
        byte[] d = XMem.Decompress(c, raw.Length);
        bool eq = d.Length == raw.Length; for (int i = 0; eq && i < d.Length; i++) if (d[i] != raw[i]) eq = false;
        Check(eq, "compress/decompress roundtrip su " + big.Name + " (orig comp " + big.CompSize + ", nuovo " + c.Length + ")");

        // 3) add files, rename
        string f1 = Path.Combine(work, "nuovo.txt"); File.WriteAllText(f1, new string('x', 50000) + "ciao");
        string f2 = Path.Combine(work, "àèì.bin"); File.WriteAllBytes(f2, new byte[0]);
        plan = new List<PlanItem>();
        foreach (ZEntry e in a.Entries) { PlanItem p = new PlanItem(); p.Src = e; plan.Add(p); }
        PlanItem n1 = new PlanItem(); n1.FilePath = f1; n1.Name = "sub/nuovo.txt"; n1.Method = 21; plan.Add(n1);
        PlanItem n2 = new PlanItem(); n2.FilePath = f2; n2.Name = "sub/àèì.bin"; n2.Method = 21; plan.Add(n2);
        PlanItem n3 = new PlanItem(); n3.FilePath = f1; n3.Name = "sub/defl.txt"; n3.Method = 8; plan.Add(n3);
        plan[0].Name = "rinominato.carbin";
        a.Rewrite(plan, null);
        a = ZArchive.Open(zip);
        Check(a.Entries.Count == plan.Count, "conteggio dopo add/rename");
        bad = 0; foreach (ZEntry e in a.Entries) if (a.Test(e) != null) bad++;
        Check(bad == 0, "tutte le entry valide dopo modifiche");
        ZEntry ne = a.Entries.Find(x => x.Name == "sub/nuovo.txt");
        Check(ne != null && ne.Method == 21 && System.Text.Encoding.ASCII.GetString(a.Read(ne)).EndsWith("ciao"), "file aggiunto XMem leggibile");
        Check(a.Entries.Find(x => x.Name == "rinominato.carbin") != null, "rinomina");
        Check(a.Entries.Find(x => x.Name == "sub/àèì.bin") != null, "nome unicode + file vuoto");

                Console.WriteLine(fails == 0 ? "TUTTO OK" : ("FALLITI: " + fails));
        return fails;
    }
}
