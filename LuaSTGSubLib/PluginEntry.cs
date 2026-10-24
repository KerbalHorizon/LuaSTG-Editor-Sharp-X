using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LuaSTGEditorSharp.Plugin;
using LuaSTGEditorSharp.Windows;
using LuaSTGEditorSharp.EditorData;
using LuaSTGEditorSharp.EditorData.Node;
using LuaSTGEditorSharp.EditorData.Document;
using LuaSTGEditorSharp.EditorData.Document.Meta;
using LuaSTGEditorSharp.EditorData.Interfaces;

namespace LuaSTGEditorSharp
{
    public class PluginEntry : AbstractPluginEntry
    {
        public PluginEntry() : base()
        {
            // Install the BridgeDeps resolver before anything else can touch Serilog/Newtonsoft.
            // The bridge assembly itself may fail to load; never let that stop the Sub plugin.
            try
            {
                LuaSTGMcpBridge.BridgeBootstrap.EnsureInstalled();
            }
            catch (Exception)
            {
            }

            NodeTypeCache = new NodeTypeCache();
            Execution = new LSTGSubExecution();

            // v0.6.0: start the local MCP bridge (127.0.0.1:8090).
            // BridgeHost.Start() already catches everything and reports failure by returning
            // false; this guard only covers a type initializer failure (e.g. a Serilog version
            // mismatch), so a broken bridge can never take the editor down with it.
            try
            {
                LuaSTGMcpBridge.BridgeHost.Start();
            }
            catch (Exception ex)
            {
                // The bridge must never take the plugin, or the editor, down with it. Logging lives
                // in the bridge assembly (BridgeLog) so this project keeps its original dependency
                // set: on an older editor the Serilog assembly itself may be the thing that is
                // missing, and a throwing logger would defeat this catch.
                try
                {
                    LuaSTGMcpBridge.BridgeLog.StartupFailure(ex);
                }
                catch (Exception)
                {
                    // The bridge assembly itself may be the thing that failed to load.
                }
            }
        }

        public override string NodeAssemblyName => "LuaSTGNode.Legacy";

        public override IInputWindowSelectorRegister GetInputWindowSelectorRegister()
        {
            return new Windows.Input.InputWindowSelectorRegister();
        }

        public override AbstractToolbox GetToolbox(IMainWindow mw)
        {
            return new PluginToolbox(mw);
        }

        public override AbstractMetaData GetMetaData()
        {
            return new MetaData();
        }

        public override AbstractMetaData GetMetaData(IMetaInfoCollection[] meta)
        {
            return new MetaData(meta);
        }

        public override IViewDefinition GetViewDefinitionWindow(DocumentData document)
        {
            return new ViewDefinition(document);
        }

        public override Type[] StageNodeType
        {
            get => new Type[] { typeof(EditorData.Node.Stage.Stage) };
        }

        public override Type[] BossSCNodeType
        {
            get => new Type[] { typeof(EditorData.Node.Boss.BossSpellCard) };
        }

        public override int[][] MetaInfoCollectionWatchDict
            => new int[][]{
                new int[]{ (int)MetaType.UserDefined },
                new int[]{ (int)MetaType.StageGroup },
                new int[]{ (int)MetaType.Boss, (int)MetaType.Bullet, (int)MetaType.BossBG
                    , (int)MetaType.Laser, (int)MetaType.BentLaser, (int)MetaType.Object },
                new int[]{ (int)MetaType.Task },
                new int[]{ (int)MetaType.ImageLoad },
                new int[]{ (int)MetaType.ImageGroupLoad },
                new int[]{ (int)MetaType.BGMLoad },
                new int[]{ (int)MetaType.FXLoad },
                new int[]{ (int)MetaType.FontLoad },
                new int[]{ (int)MetaType.TTFLoad },
                new int[]{ (int)MetaType.Item }
            };

        public override int MetaInfoCollectionTypeCount { get => (int)MetaType.__max; }

        public override string TargetLSTGVersion => "LuaSTG Sub";
    }
}
