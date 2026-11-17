using System.Runtime.CompilerServices;
namespace BepInEx.Configuration {
 public class ConfigEntry<T> { public T Value; public ConfigEntry(T value) { Value=value; } }
 public class ConfigFile {
  readonly Dictionary<string,object> values=new();
  public bool SaveOnConfigSet=true;
  public ConfigEntry<T> Bind<T>(string section,string key,T value,string description="") {
   string name=section+"/"+key;
   if(!values.TryGetValue(name,out var existing)) values[name]=existing=new ConfigEntry<T>(value);
   return (ConfigEntry<T>)existing;
  }
  public void Save() {}
 }
}
namespace UnityEngine {
 public class GameObject { public GameOptionsMenu Menu; public T GetComponent<T>() where T:class => Menu as T; }
 public static class GUIUtility { public static string systemCopyBuffer; }
 public static class Time { public static float realtimeSinceStartup; }
}
namespace Hazel {
 public enum SendOption { Reliable }
 public class MessageWriter {
  public List<byte> Bytes=new();
  public void Write(byte b)=>Bytes.Add(b);
  public void WritePacked(uint v) { while(v>=128) {Bytes.Add((byte)(v|128));v>>=7;} Bytes.Add((byte)v); }
 }
 public class MessageReader {
  readonly byte[] bytes; int at;
  public MessageReader(byte[] bytes) {this.bytes=bytes;}
  public uint ReadPackedUInt32() { uint v=0; for(int i=0;i<5;i++) {byte b=bytes[at++];v|=(uint)(b&127)<<(7*i);if(b<128)return v;}throw new Exception(); }
 }
}
namespace AmongUs.GameOptions {
 public interface IGameOptions { int Version {get;} }
 public class Options:IGameOptions { public int Version {get;set;}=8; public int Value; }
 public class Factory {
  public byte[] ToBytes(IGameOptions options,bool b)=>BitConverter.GetBytes(((Options)options).Value);
  public IGameOptions FromBytes(byte[] bytes) {if(bytes.Length!=4)throw new Exception("Blob defekt");return new Options{Value=BitConverter.ToInt32(bytes)};}
 }
 public class GameOptionsManager {
  public static GameOptionsManager Instance=new();
  public Factory gameOptionsFactory=new();
  public IGameOptions GameHostOptions=new Options(), CurrentGameOptions=new Options();
 }
}
public class TextWidget { public string text; }
public class OptionBehaviour {}
public class StringOption:OptionBehaviour { public int oldValue,Value; public TextWidget ValueText=new(); }
public class GameOptionsMenu { public List<OptionBehaviour> Children=new(); }
public class PlayerControl {
 public static List<PlayerControl> AllPlayerControls=new();
 public static PlayerControl LocalPlayer;
 public uint NetId; public byte PlayerId; public int OwnerId; public bool AmOwner;
 public void HandleRpc() {}
}
public class AmongUsClient {
 public static AmongUsClient Instance;
 public bool AmHost; public int GameId=1,HostId=1;
 public readonly List<(float At,byte[] Bytes)> Sent=new();
 public Hazel.MessageWriter StartRpcImmediately(uint net,byte id,Hazel.SendOption send,int target)=>new();
 public void FinishRpcImmediately(Hazel.MessageWriter writer)=>Sent.Add((UnityEngine.Time.realtimeSinceStartup,writer.Bytes.ToArray()));
 public void OnGameJoined() {}
}
public class PlayerPhysics { public void CoSpawnPlayer() {} }
public class HudManager { public void Update() {} }
public class MainMenuManager { public void Start() {} }
public class GameStartManager {
 public int Starts;
 public void ResetStartState() {}
 [MethodImpl(MethodImplOptions.NoInlining)] public void BeginGame() {Starts++;}
}
public class LogicOptions {
 public AmongUs.GameOptions.IGameOptions currentGameOptions=new AmongUs.GameOptions.Options();
 public void SyncOptions() {}
 public void SetGameOptions(AmongUs.GameOptions.IGameOptions options) {currentGameOptions=options;}
}
public class GameManager { public static GameManager Instance=new(); public LogicOptions LogicOptions=new(); }
namespace TheOtherRoles {
 public class TheOtherRolesPlugin {
  public static TheOtherRolesPlugin Instance=new(); public BepInEx.Configuration.ConfigFile Config=new();
  public const string VersionString="4.8.0";
 }
 public static class Helpers {public static PlayerControl playerById(byte id)=>PlayerControl.AllPlayerControls.FirstOrDefault(p=>p.PlayerId==id);}
 public enum CustomRPC:byte { ShareOptions=101 }
 public class CustomOption {
  public static List<CustomOption> options=new(); public static int preset;
  public static BepInEx.Configuration.ConfigEntry<string> vanillaSettings;
  public int id,selection,defaultSelection; public object[] selections={"Off","On"};
  public BepInEx.Configuration.ConfigEntry<int> entry; public OptionBehaviour optionBehaviour; public Action onChange;
  public CustomOption(int id,int selection=0) {this.id=id;this.selection=selection;options.Add(this);}
  public float getFloat()=>(float)selections[selection];
  [MethodImpl(MethodImplOptions.NoInlining)] public void updateSelection(int value,bool notifyUsers=true) {
   selection=value;onChange?.Invoke();
   if(id==0 && AmongUsClient.Instance.AmHost) {switchPreset(value);ShareOptionSelections();}
   if(AmongUsClient.Instance.AmHost) {
    var currentTab=((UnityEngine.GameObject)null).GetComponent<GameOptionsMenu>();
    if(currentTab!=null) currentTab.Children[0]=optionBehaviour;
   }
  }
  public static void switchPreset(int value) {
   saveVanillaOptions();preset=value;
   vanillaSettings=TheOtherRolesPlugin.Instance.Config.Bind($"Preset{preset}","GameOptions","");
   loadVanillaOptions();
  }
  public static void saveVanillaOptions()=>vanillaSettings.Value=Convert.ToBase64String(AmongUs.GameOptions.GameOptionsManager.Instance.gameOptionsFactory.ToBytes(GameManager.Instance.LogicOptions.currentGameOptions,false));
  public static bool loadVanillaOptions() {
   if(vanillaSettings.Value=="")return false;
   var opts=AmongUs.GameOptions.GameOptionsManager.Instance.gameOptionsFactory.FromBytes(Convert.FromBase64String(vanillaSettings.Value));
   GameManager.Instance.LogicOptions.SetGameOptions(opts);return true;
  }
  public static void ShareOptionSelections() {throw new Exception("Ungepatchter Burst");}
  public static byte[] serializeOptions()=>throw new Exception("Ungepatchter Codec");
  public static int deserializeOptions(byte[] inputValues)=>throw new Exception("Ungepatchter Import");
  public static void copyToClipboard()=>throw new Exception("Ungepatchtes Copy");
  public static int pasteFromClipboard()=>throw new Exception("Ungepatchtes Paste");
 }
 public static class CustomOptionHolder {
  public static CustomOption presetSelection, coreFirst,coreLast;
  public static string[] presets=Enumerable.Range(0,7).Select(i=>$"Preset{i}").ToArray();
 }
 public static class RPCProcedure {public static void HandleShareOptions(byte numberOfOptions,Hazel.MessageReader reader)=>throw new Exception("Originalempfang");}
}
namespace TheOtherRoles.Patches {
 public static class GameStartManagerPatch {
  public static class GameStartManagerBeginGame {
   public static int Decisions; public static int OriginalIndex=6;
   public static bool Prefix() {
    Decisions++;
    var roll=new Random(123).NextDouble();
    if(roll>=0) TheOtherRoles.CustomOptionHolder.presetSelection.updateSelection(OriginalIndex,true);
    return true;
   }
  }
 }
}
