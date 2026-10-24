using System.Text.Json;

namespace Ddj200;

public static class SharedFileTests
{
    public static int Run()
    {
        int passed=0;
        void Check(bool ok,string name){if(!ok)throw new InvalidOperationException("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
        string folder=Path.Combine(ProductProfile.DataRoot,"shared-file-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string status=Path.Combine(folder,"status.json");
            MidiLearn.Save(status,new{generation=1});
            using(var oldReader=new FileStream(status,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                bool refused=false;
                try{MidiLearn.Save(status,new{generation=2});}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException){refused=true;}
                Check(refused,"reproduce old status reader blocking atomic replacement on Windows");
            }
            Check(Directory.GetFiles(folder,"*.tmp-*").Length==0,"failed status replacement leaves no temporary files");
            using(var snapshot=SharedFile.OpenSnapshot(status))
            {
                string legacyTemp=Path.Combine(folder,"legacy-status.tmp");
                File.WriteAllText(legacyTemp,"{}");
                bool moveDenied=false;
                try{File.Move(legacyTemp,status,true);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){moveDenied=true;}
                Check(moveDenied,"legacy overwrite Move still fails with a delete-sharing reader; Replace is required");
                File.Delete(legacyTemp);
                MidiLearn.Save(status,new{generation=2});
                using var old=JsonDocument.Parse(snapshot);
                using var current=JsonDocument.Parse(SharedFile.ReadSnapshot(status));
                Check(old.RootElement.GetProperty("generation").GetInt32()==1 && current.RootElement.GetProperty("generation").GetInt32()==2,
                    "shared status reader retains complete old snapshot while publisher replaces it");
            }
            for(int i=3;i<103;i++)
            {
                using var snapshot=SharedFile.OpenSnapshot(status);
                MidiLearn.Save(status,new{generation=i});
                using var old=JsonDocument.Parse(snapshot);
                if(old.RootElement.GetProperty("generation").GetInt32()!=i-1)throw new InvalidOperationException("Mixed snapshot");
            }
            Check(true,"100 overlapping status read/replace cycles stay valid");

            string config=Path.Combine(folder,"config.toml");
            string original=File.ReadAllText(MicroBinding.StatePath);
            File.WriteAllText(config,original);
            using(var writer=new FileStream(config,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))
            {
                bool sharing=false;
                try{SharedFile.ReadSnapshot(config);}catch(IOException e){sharing=(e.HResult&0xffff)==32;}
                Check(sharing,"configuration snapshot refuses an active in-place writer");
                int waits=0;
                string recovered=SharedFile.OnSharingViolation(()=>SharedFile.ReadSnapshot(config),ms=>{waits++;writer.Dispose();});
                Check(waits==1 && recovered==original,"configuration read succeeds after real writer releases its lock");
                Check(Surface12Map.ConfigurationFingerprint(recovered,true)==Surface12Map.ConfigurationFingerprint(original,true),"recovered config still passes the full contract check");
            }
            using(var writer=new FileStream(config,FileMode.Open,FileAccess.Write,FileShare.None))
            {
                int waitMs=0;bool refused=false;
                try{SharedFile.OnSharingViolation(()=>SharedFile.ReadSnapshot(config),ms=>waitMs+=ms);}
                catch(IOException e){refused=(e.HResult&0xffff)==32;}
                Check(refused&&waitMs==50,"persistent writer lock fails after bounded 50 ms budget");
            }
            int unrelatedWaits=0;
            try{SharedFile.OnSharingViolation<string>(()=>throw new UnauthorizedAccessException("denied"),_=>unrelatedWaits++);}catch(UnauthorizedAccessException){}
            try{SharedFile.OnSharingViolation(()=>SharedFile.ReadSnapshot(Path.Combine(folder,"missing.toml")),_=>unrelatedWaits++);}catch(FileNotFoundException){}
            Check(unrelatedWaits==0,"permission errors and missing files are not retried or hidden");
            int removalAttempts=0;
            bool recoveredRemoval=SharedFile.OnSharingViolation(()=>{if(removalAttempts++==0)throw new IOException("busy replacement",unchecked((int)0x80070497));return true;},_=>{},replacing:true);
            Check(recoveredRemoval&&removalAttempts==2,"only safe unchanged-name replacement failure 1175 can recover");
            foreach(int code in new[]{1176,1177})
            {
                int waits=0;
                try{SharedFile.OnSharingViolation<bool>(()=>throw new IOException("partial replacement",unchecked((int)(0x80070000u|(uint)code))),_=>waits++,replacing:true);}catch(IOException){}
                Check(waits==0,"partial replacement error is never retried: "+code);
            }
            File.WriteAllText(config,original.Replace("separateMicrophoneKeys = false","separateMicrophoneKeys = true"));
            bool incompatible=false;
            try{Surface12Map.ConfigurationFingerprint(SharedFile.ReadConfiguration(config),true);}catch(InvalidDataException){incompatible=true;}
            Check(incompatible,"fresh incompatible config still disarms instead of falling back to cached content");
            Check(Directory.GetFiles(folder,"*.tmp-*").Length==0,"successful status replacements leave no temporary files");
            Console.WriteLine($"{passed} shared file checks passed; isolated files only");
            return 0;
        }
        finally { Directory.Delete(folder,true); }
    }
}
