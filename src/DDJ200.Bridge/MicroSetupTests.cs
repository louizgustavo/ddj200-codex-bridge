using System.Text;

namespace Ddj200;

public static class MicroSetupTests
{
    public static int Run()
    {
        int count=0;
        void Check(bool ok,string label) { if(!ok)throw new InvalidOperationException("Setup test failed: "+label); count++; }
        void Refuses(string text,string label)
        {
            bool refused=false;try{MicroSetup.PrepareText(text);}catch(Exception e) when(e is InvalidDataException or FormatException or System.Text.Json.JsonException){refused=true;}
            Check(refused,label);
        }
        string fresh=MicroSetup.PrepareText("");
        MicroSetup.Validate(fresh);
        Check(fresh.Contains("codex-micro-agent-source = \"recent\""),"fresh setup has explicit source");
        Check(MicroSetup.PrepareText(fresh)==fresh,"setup is idempotent");
        string pinned=fresh.Replace("\"recent\"","\"pinned\"").Replace("keycapId = \"FAST\"","keycapId = \"FAST\"\ncommandId = \"toggleThreadPin\"");
        Check(MicroSetup.PrepareText(pinned)==pinned,"pinned source and custom actions preserved byte for byte");
        string unrelated="model = \"example\"\n[desktop]\nfontSize = 14\n[projects.abc]\ntrust_level = \"trusted\"\n";
        string prepared=MicroSetup.PrepareText(unrelated);
        Check(prepared.Contains("fontSize = 14\n[projects.abc]\ntrust_level = \"trusted\""),"unrelated sections preserved");
        Check(prepared.Contains("[desktop]\ncodex-micro-agent-source"),"source inserted in its own table");
        string sourceOnly="[desktop]\ncodex-micro-agent-source = \"pinned\"\n";
        Check(MicroSetup.PrepareText(sourceOnly).StartsWith(sourceOnly),"source-only setup preserves pinned");
        Refuses(fresh.Replace("\"recent\"","\"custom\""),"unsupported source not silently replaced");
        Refuses(fresh.Replace("separateMicrophoneKeys = false","separateMicrophoneKeys = true"),"incompatible layout not overwritten");
        Refuses("[desktop]\ncodex-micro-agent-source = \"pinned\"\n[desktop.codex-micro-layout]\nversion = 2\n","partial/new layout preserved");
        Refuses("desktop = { other = 1 }","inline desktop refused without corruption");
        Refuses("[desktop]\n[desktop]\n","duplicate desktop refused");
        Refuses("note = \"\"\"\n[desktop]\n\"\"\"\n","multiline data not mistaken for table");
        Refuses("name = \"unfinished","invalid input left intact");
        Refuses("[desktop ]\nfontSize = 12","alternate table syntax never duplicated");
        Refuses("desktop.other = 1","dotted desktop never duplicated");
        string root=Path.Combine(Path.GetTempPath(),"ddj-setup-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"config.toml");
            Check(MicroSetup.PrepareFile(path),"missing config created");
            Check(!MicroSetup.PrepareFile(path),"no second write");
            File.WriteAllText(path,unrelated,new UTF8Encoding(true));byte[] original=File.ReadAllBytes(path);
            Check(MicroSetup.PrepareFile(path),"existing config supplemented");
            Check(Directory.GetFiles(root,"*.before-ddj-*").Select(File.ReadAllBytes).Any(x=>x.SequenceEqual(original)),"exact backup including BOM");
            File.WriteAllText(path,pinned);byte[] before=File.ReadAllBytes(path);
            Check(!MicroSetup.PrepareFile(path)&&File.ReadAllBytes(path).SequenceEqual(before),"existing pinned config never rewritten");
            File.WriteAllText(path,"[desktop.codex-micro-layout]\nversion = 2");before=File.ReadAllBytes(path);
            try{MicroSetup.PrepareFile(path);}catch(InvalidDataException){}
            Check(File.ReadAllBytes(path).SequenceEqual(before),"failure leaves original file intact");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {count} setup checks (no driver, MIDI, app or live configuration touched)");return 0;
    }
}
