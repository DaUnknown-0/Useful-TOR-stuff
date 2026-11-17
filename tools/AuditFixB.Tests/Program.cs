using System.Reflection;
using HarmonyLib;
using Hazel;
using TheOtherRoles;
using UsefulTORStuff;
using UnityEngine;
using AmongUs.GameOptions;

namespace UsefulTORStuff {
 public static class UsefulTORStuffPlugin {public static TestLog Logger=new();}
 public class TestLog {public void LogInfo(string s){} public void LogWarning(string s){} public void LogError(string s){} }
 public static class DraftLawyerConflict {public static int SelectionForSync(CustomOption o)=>o.selection;}
 public static class UsefulVersionHandshake {public static Dictionary<int,object> playerVersions=new();}
 public static class TorType {public static Type Get(string name)=>typeof(CustomOption).Assembly.GetType(name);}
}
class Program {
 static int checks;
 static void Check(bool value,string name) {checks++;if(!value)throw new Exception("FEHLER: "+name);}
 static void Throws(Action action,string name) {try{action();throw new Exception("Nicht abgewiesen: "+name);}catch(InvalidDataException){}catch(EndOfStreamException){}checks++;}
 static object Call(Type type,string nested,string name,params object[] args)=>type.GetNestedType(nested,BindingFlags.NonPublic).GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,args);
 static void Tick()=>Call(typeof(PacedOptionSync),"TickPatch","Postfix");
 static byte[] Block(params (int Id,uint Sel)[] rows) {var w=new MessageWriter();foreach(var row in rows){w.WritePacked((uint)row.Id);w.WritePacked(row.Sel);}return w.Bytes.ToArray();}
 static List<(int Id,int Selection)> Read(byte[] bytes) {
  var reader=new MessageReader(bytes.Skip(1).ToArray());var rows=new List<(int,int)>();
  for(int i=0;i<bytes[0];i++) rows.Add(((int)reader.ReadPackedUInt32(),(int)reader.ReadPackedUInt32()));
  return rows;
 }
 static void Drain() {for(int i=0;i<2000&&PacedOptionSync.Sending;i++){Time.realtimeSinceStartup+=.16f;Tick();}Check(!PacedOptionSync.Sending,"Queue vollständig geleert");}
 static void Main() {
  Codec();
  RealTorAnchors();
  AmongUsClient.Instance=new AmongUsClient{AmHost=true};
  PlayerControl.LocalPlayer=new PlayerControl{AmOwner=true,OwnerId=1};
  PlayerControl.AllPlayerControls.Add(PlayerControl.LocalPlayer);PlayerControl.AllPlayerControls.Add(new PlayerControl{OwnerId=2});
  CustomOptionHolder.presetSelection=new CustomOption(0){selections=CustomOptionHolder.presets.Cast<object>().ToArray()};
  CustomOptionHolder.coreFirst=new CustomOption(10,1);
  _=OtherMod.First;
  UTSGate.BeginOptionCapture();
  var own=new CustomOption(1301,1);var missing=new CustomOption(1302,1);
  UTSGate.EndOptionCapture();
  CustomOptionHolder.coreLast=new CustomOption(11,1);
  var unknown=new CustomOption(99001,1);var unknown2=new CustomOption(99002,1);
  CustomOption.vanillaSettings=TheOtherRolesPlugin.Instance.Config.Bind("Preset0","VanillaOptions","");
  var harmony=new Harmony("AuditFixB.Tests");
  harmony.PatchAll(Assembly.GetExecutingAssembly());
  Ownership(own,unknown,unknown2);
  ReceiveAndGate(own,missing);
  Clipboard(own);
  QueueLifecycle();
  Start(harmony);
  Console.WriteLine($"{checks} Prüfungen erfolgreich, reale HarmonyX-Patches auf isolierten TOR-/Spiel-Stubs, kein Spielstart.");
 }
 static void RealTorAnchors() {
  string path=Path.GetFullPath("../TheOtherRoles-main/TheOtherRoles-main/TheOtherRoles/bin/Debug/net6.0/TheOtherRoles.dll");
  using var module=Mono.Cecil.ModuleDefinition.ReadModule(path);
  var option=module.GetType("TheOtherRoles.CustomOption");
  var update=option.Methods.Single(m=>m.Name=="updateSelection");
  Check(update.Body.Instructions.Count(i=>i.Operand is Mono.Cecil.GenericInstanceMethod m
    &&m.DeclaringType.FullName=="UnityEngine.GameObject"&&m.Name=="GetComponent"
    &&m.GenericArguments.Single().FullName=="GameOptionsMenu")==1,"TOR-023-Anker an echter TOR-DLL");
  var begin=module.GetType("TheOtherRoles.Patches.GameStartManagerPatch").NestedTypes.Single(t=>t.Name=="GameStartManagerBeginGame").Methods.Single(m=>m.Name=="Prefix");
  Check(begin.Body.Instructions.Count(i=>i.Operand is Mono.Cecil.MethodReference m
    &&m.DeclaringType.FullName=="TheOtherRoles.CustomOption"&&m.Name=="updateSelection")==1,"TOR-003-Presetanker an echter TOR-DLL");
  Check(begin.Body.Instructions.Count(i=>i.Operand is Mono.Cecil.MethodReference m
    &&m.DeclaringType.FullName=="System.Random"&&m.Name=="NextDouble")==1,"Zufallsanker an echter TOR-DLL");
 }
 static void Codec() {
  foreach(int selection in new[]{0,1,127,128,255,256,720,int.MaxValue}) {
   var expected=new[]{(1,selection),(2,selection),(65535,selection),(80001,selection)};
   var encoded=AuditFixB_OptionCodec.Encode(expected);
   Check(AuditFixB_OptionCodec.Decode(encoded).SequenceEqual(expected),$"Codec {selection}");
  }
  var legacy=AuditFixB_OptionCodec.Encode(new[]{(1,127),(2,0),(65535,5)});
  Check(!AuditFixB_OptionCodec.Extended(legacy),"Kleine Werte bleiben TOR-kompatibel");
  Check(legacy.SequenceEqual(new byte[]{127,1,0,128,5,255,255}),"TOR-Originalformat bytegenau");
  var large=AuditFixB_OptionCodec.Encode(new[]{(1,720),(2,128)});
  Check(AuditFixB_OptionCodec.Extended(large),"Große Werte versioniert");
  for(int i=1;i<large.Length;i++) {var truncated=large.Take(i).ToArray();Throws(()=>AuditFixB_OptionCodec.Decode(truncated),"Abgeschnittener Datensatz");}
  Throws(()=>AuditFixB_OptionCodec.Decode(new byte[]{0,1}),"Kurze Alt-ID");
  Throws(()=>AuditFixB_OptionCodec.Decode(new byte[]{0,1,0,0,1,0}),"Doppelte Alt-ID");
  Throws(()=>AuditFixB_OptionCodec.Decode(new byte[]{128}),"Alt-ID 0");
  Throws(()=>AuditFixB_OptionCodec.Encode(new[]{(1,-1)}),"Negativer Index");
  Throws(()=>AuditFixB_OptionCodec.Encode(new[]{(1,1),(1,0)}),"Doppelte neue ID");
  var random=new Random(135);
  for(int test=0;test<100;test++) {
   var rows=Enumerable.Range(1,100).Select(i=>(i*1023,random.Next(2000))).ToArray();
   Check(AuditFixB_OptionCodec.Decode(AuditFixB_OptionCodec.Encode(rows)).SequenceEqual(rows),"100 Werte Roundtrip");
  }
 }
 static void Ownership(CustomOption own,CustomOption unknown,CustomOption unknown2) {
  // Die Produktionsqueue enthält jetzt eine KOPIE des globalen Registers.
  CustomOption.ShareOptionSelections();
  Check(OptionSyncFix.OwnerGroup(CustomOptionHolder.coreFirst)==-1,"TOR bleibt Kern");
  Check(OptionSyncFix.OwnerGroup(own)>=0,"UTS ist kein TOR");
  Check(OptionSyncFix.OwnerGroup(OtherMod.First)!=OptionSyncFix.OwnerGroup(own),"Queue kontaminiert UTS-Besitz nicht");
  Check(OptionSyncFix.OwnerGroup(OtherMod.First)==OptionSyncFix.OwnerGroup(OtherMod.Last),"Fremde Moddefinitionen gruppiert");
  Check(OptionSyncFix.OwnerGroup(unknown)!=OptionSyncFix.OwnerGroup(unknown2),"Unbekannte einzeln isoliert");
  Drain();
  var packets=AmongUsClient.Instance.Sent;
  Check(packets.All(p=>Read(p.Bytes).Select(r=>OptionSyncFix.OwnerGroup(CustomOption.options.First(o=>o.id==r.Id))).Distinct().Count()==1),"Kein Paket mischt Besitzer");
  var torValues=new Dictionary<int,int>();
  foreach(var packet in packets) foreach(var row in Read(packet.Bytes)) {
   var option=CustomOption.options.First(o=>o.id==row.Id);
   if(OptionSyncFix.OwnerGroup(option)!=-1)break; // TOR: First wirft, Rest des Blocks verloren.
   torValues[row.Id]=row.Selection;
  }
  Check(torValues[10]==1&&torValues[11]==1,"Reiner TOR-Gast erhält beide Kernoptionen trotz eingefügter Modoption");
  Check(packets.Zip(packets.Skip(1),(a,b)=>b.At-a.At).All(g=>g>=.149f),"Paketabstand erhalten");
  UTSGate.BeginOptionCapture();for(int i=0;i<650;i++)new CustomOption(2000+i,1);UTSGate.EndOptionCapture();
  packets.Clear();CustomOption.ShareOptionSelections();Drain();
  Check(packets.All(p=>p.Bytes.Length<=900&&p.Bytes[0]<=200),"Byte- und Anzahlbudget eingehalten");
  // Werte erst beim Versand lesen.
  packets.Clear();CustomOption.ShareOptionSelections();own.selection=0;Drain();
  Check(packets.SelectMany(p=>Read(p.Bytes)).Single(r=>r.Id==own.id).Selection==0,"Kein alter Queue-Snapshot überschreibt Änderungen");
 }
 static void QueueLifecycle() {
  Drain();
  // Große pluginübergreifende IDs plus mehrbyteige Selections erzwingen die Bytegrenze.
  UTSGate.BeginOptionCapture();
  for(int i=0;i<150;i++) new CustomOption(1000000000+i,720){selections=Enumerable.Range(0,721).Cast<object>().ToArray()};
  UTSGate.EndOptionCapture();
  var sent=AmongUsClient.Instance.Sent;sent.Clear();
  int estimated=PacedOptionSync.SendPaced(CustomOption.options.Where(o=>o.id>=1000000000));Drain();
  Check(estimated==sent.Count&&estimated==2,"900-Byte-Grenze bestimmt auch die gemeldete Paketzahl");
  Check(sent.All(p=>p.Bytes.Length<=900),"Große IDs und Selections bleiben unter Bytegrenze");
  // Join-Welle: Nachruhe von einer Sekunde und gemeinsame Queue.
  typeof(PacedOptionSync).GetField("installed",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,true);
  sent.Clear();Call(typeof(PacedOptionSync),"SpawnPatch","Postfix");
  Time.realtimeSinceStartup+=.5f;Call(typeof(PacedOptionSync),"SpawnPatch","Postfix");
  Time.realtimeSinceStartup+=.9f;Tick();
  Check(sent.Count==0,"Join-Welle wartet auf eine Sekunde Ruhe");
  Time.realtimeSinceStartup+=.11f;Tick();
  Check(sent.Count==1,"Join-Welle startet mit genau einem Paket");Drain();
  sent.Clear();
  for(int i=0;i<9;i++){Call(typeof(PacedOptionSync),"SpawnPatch","Postfix");Time.realtimeSinceStartup+=.51f;Tick();}
  Check(sent.Count>0,"Dauernde Joins verhindern Sync nicht länger als vier Sekunden");Drain();
  // Ein ausstehender alter Dump darf weder Lobby- noch Hostwechsel überleben.
  sent.Clear();CustomOption.ShareOptionSelections();AmongUsClient.Instance.GameId++;
  Time.realtimeSinceStartup+=.2f;Tick();
  Check(!PacedOptionSync.Sending&&sent.Count==0,"Lobbywechsel verwirft alte Queue");
  CustomOption.ShareOptionSelections();AmongUsClient.Instance.HostId++;
  Time.realtimeSinceStartup+=.2f;Tick();
  Check(!PacedOptionSync.Sending&&sent.Count==0,"Hostwechsel verwirft alte Queue");
  AmongUsClient.Instance.HostId=1;
 }
 static void ReceiveAndGate(CustomOption own,CustomOption missing) {
  AmongUsClient.Instance.AmHost=false;AmongUsClient.Instance.OnGameJoined();
  UsefulVersionHandshake.playerVersions[1]=new();UTSGate.EvaluateInLobby();
  own.selection=missing.selection=1;
  Check(!UTSGate.Bool(own)&&!UTSGate.Bool(missing),"Älterer Host: lokale gespeicherte Regeloptionen bleiben gesperrt");
  RPCProcedure.HandleShareOptions(3,new MessageReader(Block((own.id,1),(999999,0),(10,0))));
  Check(UTSGate.Bool(own),"Empfangene Option freigegeben");
  Check(!UTSGate.Bool(missing),"Nicht empfangene Option bleibt gesperrt");
  Check(CustomOptionHolder.coreFirst.selection==0,"Bekannte ID nach unbekannter ID angewandt");
  RPCProcedure.HandleShareOptions(2,new MessageReader(Block((own.id,999),(10,1))));
  Check(own.selection==1&&CustomOptionHolder.coreFirst.selection==1,"Ungültiger Index übersprungen, Rest empfangen");
  RPCProcedure.HandleShareOptions(2,new MessageReader(Block((own.id,0))));
  Check(own.selection==1,"Abgeschnittener Block wird vor Anwendung abgewiesen");
  AmongUsClient.Instance.HostId=3;
  Check(!UTSGate.Bool(own),"Hostwechsel löscht Optionsfreigaben");
  AmongUsClient.Instance.HostId=1;AmongUsClient.Instance.AmHost=true;
  Check(UTSGate.Bool(own),"Host liest eigene Werte direkt");
 }
 static void Clipboard(CustomOption own) {
  own.selection=1;
  byte[] bad=AuditFixB_OptionCodec.Encode(new[]{(own.id,0),(1302,127)});
  Check(CustomOption.deserializeOptions(bad)==0&&own.selection==1,"Ungültiger bekannter Index importiert nichts");
  byte[] good=AuditFixB_OptionCodec.Encode(new[]{(own.id,0),(999999,999)});
  Check(CustomOption.deserializeOptions(good)==2&&own.selection==0,"Unbekannte Clipboard-ID stört bekannte nicht");
  Check(own.entry.Value==0,"Import wird gespeichert");
  var cooldown=CustomOption.options.First(o=>o.id==2000);cooldown.selections=Enumerable.Range(0,721).Cast<object>().ToArray();cooldown.selection=720;
  GameManager.Instance.LogicOptions.currentGameOptions=new Options{Value=123};
  CustomOption.copyToClipboard();string copied=GUIUtility.systemCopyBuffer;
  Check(copied.Contains("!UTS2:"),"Erweitertes Clipboard hat expliziten Marker für alte TOR-Importer");
  cooldown.selection=0;
  Check(CustomOption.pasteFromClipboard()==3&&cooldown.selection==720,"Großer Cooldown inklusive Vanilla roundtrip");
  Check(((Options)GameManager.Instance.LogicOptions.currentGameOptions).Value==123,"Aktuelle Vanilla-Werte kopiert");
  var parts=copied.Split('!');GUIUtility.systemCopyBuffer=parts[0]+"!"+parts[1]+"!AQ==";
  cooldown.selection=0;
  Check(CustomOption.pasteFromClipboard()==0&&cooldown.selection==0,"Defektes Vanilla-Clipboard übernimmt keine Modoptionen");
  Check(CustomOption.deserializeOptions(AuditFixB_OptionCodec.Encode(new[]{(cooldown.id,720)}))==2,"Direkter neuer Deserialisierer");
  // TOR-045: Migration erfolgt vor loadVanillaOptions, kanonischer Eintrag gewinnt danach.
  var config=TheOtherRolesPlugin.Instance.Config;
  config.Bind("Preset6","VanillaOptions","").Value=Convert.ToBase64String(BitConverter.GetBytes(456));
  CustomOption.switchPreset(6);
  Check(((Options)GameManager.Instance.LogicOptions.currentGameOptions).Value==456,"Legacy-Ziel beim ersten Wechsel sofort geladen");
  config.Bind("Preset0","GameOptions","").Value=Convert.ToBase64String(BitConverter.GetBytes(789));
  config.Bind("Preset0","VanillaOptions","").Value=Convert.ToBase64String(BitConverter.GetBytes(111));
  CustomOption.switchPreset(0);
  Check(((Options)GameManager.Instance.LogicOptions.currentGameOptions).Value==789,"GameOptions hat Vorrang vor altem VanillaOptions");
 }
 public static bool RefuseStart()=>false;
 static void Start(Harmony harmony) {
  Drain();
  Check(CustomOptionHolder.presetSelection.selections.Length==8,"Fungle-Profil ergänzt");
  Check(AuditFixB_Presets.RandomPreset(6)==7&&AuditFixB_Presets.RandomPreset(7)==6,"Fungle und Submerged explizit zugeordnet");
  var tor=typeof(TheOtherRoles.Patches.GameStartManagerPatch.GameStartManagerBeginGame);
  harmony.Patch(AccessTools.Method(typeof(GameStartManager),nameof(GameStartManager.BeginGame)),prefix:new HarmonyMethod(tor,"Prefix"));
  var start=new GameStartManager();var packets=AmongUsClient.Instance.Sent;packets.Clear();
  start.BeginGame();
  Check(start.Starts==0&&PacedOptionSync.Sending,"Start wartet auf das Random-Map-Preset");
  for(int i=0;i<2000&&start.Starts==0;i++){Time.realtimeSinceStartup+=.16f;Tick();}
  Check(start.Starts==1&&!PacedOptionSync.Sending,"Start genau einmal nach gepactem Sync fortgesetzt");
  Check(CustomOption.preset==7,"Fungle lädt angehängtes Profil");
  Check(packets.Zip(packets.Skip(1),(a,b)=>b.At-a.At).All(g=>g>=.149f),"Start verursacht keinen Burst");
  TheOtherRoles.Patches.GameStartManagerPatch.GameStartManagerBeginGame.OriginalIndex=7;
  start.BeginGame();for(int i=0;i<2000&&PacedOptionSync.Sending;i++){Time.realtimeSinceStartup+=.16f;Tick();}
  Check(CustomOption.preset==6,"Submerged behält bestehendes Profil6");
  Time.realtimeSinceStartup+=.16f;Tick();
  // Ein fremder Prefix lehnt den Start ab. HarmonyX darf daraus keinen automatischen Start machen.
  var blocker=new Harmony("AuditFixB.Blocker");
  blocker.Patch(AccessTools.Method(typeof(GameStartManager),nameof(GameStartManager.BeginGame)),
    prefix:new HarmonyMethod(typeof(Program),nameof(RefuseStart)){priority=Priority.Normal});
  CustomOption.ShareOptionSelections();int before=start.Starts;start.BeginGame();Drain();
  Time.realtimeSinceStartup+=.16f;Tick();
  Check(start.Starts==before&&!PacedOptionSync.StartDeferred,"Abgelehnter Start wird nicht automatisch wiederholt");
  blocker.UnpatchSelf();
  CustomOption.ShareOptionSelections();start.BeginGame();
  Check(PacedOptionSync.StartDeferred,"Start erneut vorgemerkt");
  start.ResetStartState();Drain();Time.realtimeSinceStartup+=.16f;Tick();
  Check(start.Starts==before&&!PacedOptionSync.StartDeferred,"Stop/Reset verwirft vorgemerkten Start");
 }
}
