using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArvinCombat.EditorTools
{
    /// <summary>
    /// One-click project setup:
    ///  - configures imported animations (looping, compression) and textures (small for WebGL)
    ///  - builds an Animator Controller + prefab per warrior from the animation packs
    ///  - creates the arena scene and adds it to Build Settings
    ///  - applies light-weight WebGL player settings
    /// Menu: ArvinCombat > Setup Project. It also runs once automatically when the project first opens.
    /// </summary>
    public static class ArvinCombatSetup
    {
        const string Root = "Assets/ArvinCombat";
        const string Gen = Root + "/Generated";
        const string ResDir = Root + "/Resources/ArvinCombat";
        const string RegistryPath = ResDir + "/WarriorRegistry.asset";
        const string ScenePath = Root + "/Scenes/Main.unity";
        public const int SetupVersion = 5; // v2: jump clips, v3: online + web, v4: 8 directional moves
        const string WebBuildDir = "Builds/WebGL";                          // web client (upload to the host)
        const string RoomBuildPath = "Builds/RoomServer/ArvinCombatRoom.x86_64"; // Linux dedicated room server

        // ---------------------------------------------------------------- clip paths
        const string K = "Assets/ExplosiveLLC/Karate Warrior Mecanim Animation Pack/Animations/Karate@";
        const string S = "Assets/ExplosiveLLC/Spearman Warrior Mecanim Animation Pack/Animations/Spearman@";
        const string A = "Assets/ARPGWarrior/Animations/Humanoid/ARPG_Warrior_";
        const string SR = "Assets/SamuraiAnimsetPro/Animations/RootPlace/";
        const string SI = "Assets/SamuraiAnimsetPro/Animations/InPlace/";
        const string F = "Assets/Fantacode Studios/Melee Combat System/Animations/Attacks/";
        const string FL = "Assets/Fantacode Studios/Melee Combat System/Animations/Locomotion/";

        static string Kf(string n) => K + n + ".FBX";
        static string Sf(string n) => S + n + ".FBX";
        static string Af(string n) => A + n + ".anim";
        static string SRf(string n) => SR + n + "_Anim.FBX";
        static string SIf(string n) => SI + n + "_Anim.FBX";
        static string Ff(string n) => F + n + ".fbx";
        static string FLf(string n) => FL + n + ".fbx";

        static readonly string[] LocoKeys = { "Idle", "WalkF", "WalkB", "WalkL", "WalkR", "Run" };
        static readonly HashSet<string> LoopKeys = new HashSet<string> { "Idle", "WalkF", "WalkB", "WalkL", "WalkR", "Run", "Block", "Stun" };

        class WarriorCfg
        {
            public WarriorId id;
            public string model;
            public string forceMaterial;    // assigned to every slot (single-material models)
            public string fallbackMaterial; // assigned to empty/default slots
            public string weaponModel;
            public string weaponBone;
            public string weaponMaterial;
            public Vector3 weaponPos;
            public Vector3 weaponEuler;
            public Color tint = Color.white;
            public float scale = 1f;
            public Dictionary<string, string[]> clips = new Dictionary<string, string[]>();

            public WarriorCfg C(string key, params string[] paths) { clips[key] = paths; return this; }
        }

        static List<WarriorCfg> Configs()
        {
            var list = new List<WarriorCfg>();

            // Samurai: Samurai Animset model + katana on the Weapon_R bone
            list.Add(new WarriorCfg
            {
                id = WarriorId.Samurai,
                model = "Assets/SamuraiAnimsetPro/Models/Armature.fbx",
                weaponModel = "Assets/SamuraiAnimsetPro/Models/Weapon_Uchigatana.FBX",
                weaponBone = "Weapon_R",
                weaponMaterial = "Assets/SamuraiAnimsetPro/Materials/M_Sword.mat",
            }
            .C("Idle", SIf("Idle")).C("WalkF", SRf("Walk_F")).C("WalkB", SRf("Walk_B")).C("WalkL", SRf("Walk_L")).C("WalkR", SRf("Walk_R")).C("Run", SRf("Run_F"))
            .C("HL", SRf("Attack_Combo_01_01")).C("HR", SRf("Attack_Combo_03_01")).C("HF", SRf("Attack_Combo_02_02")).C("HB", SRf("Attack_Rising"))
            .C("KL", SRf("Attack_Combo_01_03")).C("KR", SRf("Attack_Combo_02_01")).C("KF", SRf("Attack_Combo_01_04")).C("KB", SRf("Attack_Combo_03_05"))
            .C("Special", SRf("Attack_Sprint"))
            .C("Block", SIf("Block_Hold")).C("BlockHit", SIf("Block_Hit")).C("Hit", SRf("Hit_01_F"))
            .C("HitHeavy", Ff("Finisher Kick Reaction"), Kf("Death")).C("GetUp", Ff("Getting Up"))
            .C("Stun", SIf("Weak_01_loop_01"), Kf("Stunned"))
            .C("DodgeF", SRf("Dodge_F")).C("DodgeB", SRf("Dodge_B")).C("DodgeL", SRf("Dodge_L")).C("DodgeR", SRf("Dodge_R"))
            .C("Jump", SR.Replace("RootPlace", "Montage") + "Jump_Anim.FBX", Kf("Jump")).C("JumpAttack", SRf("Air_Attack_01"), SRf("Attack_Combo_01_01"))
            .C("Death", SRf("Dead")));

            // Karate
            list.Add(new WarriorCfg
            {
                id = WarriorId.Karate,
                model = "Assets/ExplosiveLLC/Karate Warrior Mecanim Animation Pack/Characters/Karate.FBX",
                forceMaterial = "Assets/ExplosiveLLC/Karate Warrior Mecanim Animation Pack/Materials/Karate.mat",
            }
            .C("Idle", Kf("Idle")).C("WalkF", Kf("WalkForward")).C("WalkB", Kf("WalkBackward")).C("WalkL", Kf("WalkLeft")).C("WalkR", Kf("WalkRight")).C("Run", Kf("Run"))
            .C("HL", Kf("Attack4")).C("HR", Kf("Attack5")).C("HF", Kf("Attack6")).C("HB", Kf("Attack8"))
            .C("KL", Kf("Attack1")).C("KR", Kf("Attack7")).C("KF", Kf("MoveAttack1"), Kf("Attack2")).C("KB", Kf("Attack3"))
            .C("Special", Kf("Jump-Attack1"), Kf("SpecialAttack1"))
            .C("Block", Kf("Block")).C("BlockHit", Kf("BlockHitReact")).C("Hit", Kf("LightHit"))
            .C("HitHeavy", Kf("Death")).C("GetUp", Kf("Revive")).C("Stun", Kf("Stunned"))
            .C("DodgeF", Kf("DashForward")).C("DodgeB", Kf("DashBackward")).C("DodgeL", Kf("DashLeft")).C("DodgeR", Kf("DashRight"))
            .C("Jump", Kf("Jump")).C("JumpAttack", Kf("Jump-Attack1"))
            .C("Death", Kf("Death")));

            // Spearman
            list.Add(new WarriorCfg
            {
                id = WarriorId.Spearman,
                model = "Assets/ExplosiveLLC/Spearman Warrior Mecanim Animation Pack/Characters/Spearman.FBX",
                forceMaterial = "Assets/ExplosiveLLC/Spearman Warrior Mecanim Animation Pack/Materials/Spearman.mat",
            }
            .C("Idle", Sf("Idle")).C("WalkF", Sf("WalkForward")).C("WalkB", Sf("WalkBackward")).C("WalkL", Sf("WalkLeft")).C("WalkR", Sf("WalkRight")).C("Run", Sf("Run"))
            .C("HL", Sf("Attack1")).C("HR", Sf("Attack2")).C("HF", Sf("Attack4")).C("HB", Sf("Attack5"))
            .C("KL", Sf("Attack3")).C("KR", Sf("RangeAttack1"), Sf("Attack2")).C("KF", Sf("MoveAttack1"), Sf("Attack4")).C("KB", Sf("Attack5"))
            .C("Special", Sf("SpecialAttack1"))
            .C("Block", Sf("Block")).C("BlockHit", Sf("BlockHitReact")).C("Hit", Sf("LightHit"))
            .C("HitHeavy", Sf("Death")).C("GetUp", Sf("Revive")).C("Stun", Sf("Stunned"))
            .C("DodgeF", Sf("DashForward")).C("DodgeB", Sf("DashBackward")).C("DodgeL", Sf("DashLeft")).C("DodgeR", Sf("DashRight"))
            .C("Jump", Sf("Jump")).C("JumpAttack", Sf("Jump Attack1"), Sf("Attack1"))
            .C("Death", Sf("Death")));

            // Knight (ARPG Warrior: sword + shield)
            list.Add(new WarriorCfg
            {
                id = WarriorId.Knight,
                model = "Assets/ARPGWarrior/Animations/Humanoid/ARPG_Warrior.fbx",
                fallbackMaterial = "Assets/ARPGWarrior/Materials/Warrior.mat",
            }
            .C("Idle", Af("Idle1")).C("WalkF", Af("Walk_Forward_IPC")).C("WalkB", Af("Walk_Backward_IPC")).C("WalkL", Af("Walk_Leftward_IPC")).C("WalkR", Af("Walk_Rightward_IPC")).C("Run", Af("Run_Forward_IPC"))
            .C("HL", Af("Attack_Combo1")).C("HR", Af("Attack_Combo2")).C("HF", Af("Attack_Combo3")).C("HB", Af("Attack_Heavy1"))
            .C("KL", Af("Attack_Dodge"), Af("Attack_Combo4")).C("KR", Af("Attack_Dodge_to_Combo1"), Af("Attack_Combo4")).C("KF", Af("Attack_Sprint")).C("KB", Af("Attack_Combo4"))
            .C("Special", Af("Attack_Heavy2"))
            .C("Block", Af("Guard")).C("BlockHit", Af("Guard_Hit1")).C("Hit", Af("Hit1"))
            .C("HitHeavy", Af("Hit_knockdown")).C("Stun", Kf("Stunned"), Af("Guard_Break"))
            .C("DodgeF", Af("Evade_Forward")).C("DodgeB", Af("Evade_Backward")).C("DodgeL", Af("Evade_Left")).C("DodgeR", Af("Evade_Right"))
            .C("Jump", Af("Airborne"), Kf("Jump")).C("JumpAttack", Af("Attack_Airborne_End"), Af("Attack_Combo1"))
            .C("Death", Af("Death")));

            // Boxer: FS boxing animations on the mannequin model (red tint)
            list.Add(new WarriorCfg
            {
                id = WarriorId.Boxer,
                model = "Assets/SamuraiAnimsetPro/Models/Armature.fbx",
                tint = new Color(1f, 0.55f, 0.45f),
            }
            .C("Idle", FLf("Combat Idle")).C("WalkF", FLf("Walk Fwd")).C("WalkB", FLf("Walk back")).C("WalkL", FLf("Walk Left")).C("WalkR", FLf("Walk Right")).C("Run", SRf("Run_F"))
            .C("HL", Ff("Left Hook")).C("HR", Ff("Right Hook")).C("HF", Ff("Right Punch")).C("HB", Ff("Uppercut"))
            .C("KL", Ff("Left Hook - 1")).C("KR", Ff("Right Hook - 1")).C("KF", Ff("Right Hook - 2"), Ff("Right Hook - 1")).C("KB", Ff("Left Hook Counter"), Ff("Left Hook - 1"))
            .C("Special", Ff("Uppercut Combo"), Ff("Uppercut"))
            .C("Block", Ff("Block")).C("BlockHit", Ff("Block Hit")).C("Hit", Ff("Left Hit"))
            .C("HitHeavy", Ff("Finisher Kick Reaction"), Kf("Death")).C("GetUp", Ff("Getting Up")).C("Stun", Kf("Stunned"))
            .C("DodgeF", Kf("DashForward")).C("DodgeB", Kf("DashBackward")).C("DodgeL", Kf("DashLeft")).C("DodgeR", Kf("DashRight"))
            .C("Jump", Kf("Jump")).C("JumpAttack", Ff("Flying Kick"), Kf("Jump-Attack1"))
            .C("Death", Kf("Death")));

            // Wrestler: big, dark-tinted karate body with FS kicks and takedowns
            list.Add(new WarriorCfg
            {
                id = WarriorId.Wrestler,
                model = "Assets/ExplosiveLLC/Karate Warrior Mecanim Animation Pack/Characters/Karate.FBX",
                forceMaterial = "Assets/ExplosiveLLC/Karate Warrior Mecanim Animation Pack/Materials/Karate.mat",
                tint = new Color(0.55f, 0.42f, 0.38f),
                scale = 1.12f,
            }
            .C("Idle", FLf("Combat Idle")).C("WalkF", FLf("Walk Fwd")).C("WalkB", FLf("Walk back")).C("WalkL", FLf("Walk Left")).C("WalkR", FLf("Walk Right")).C("Run", Kf("Run"))
            .C("HL", Ff("Left Hook")).C("HR", Ff("Right Hook - 1")).C("HF", Ff("Right Punch")).C("HB", Ff("Take Down 2"), Ff("Uppercut"))
            .C("KL", Ff("Mma Kick")).C("KR", Ff("Mma Kick Reverse")).C("KF", Ff("Flying Kick")).C("KB", Ff("Finisher Kick"))
            .C("Special", Ff("Take Down 1"), Ff("Synced Attack"))
            .C("Block", Ff("Block")).C("BlockHit", Ff("Block Hit")).C("Hit", Ff("Right Hit"))
            .C("HitHeavy", Ff("Finisher Kick Reaction"), Kf("Death")).C("GetUp", Ff("Getting Up")).C("Stun", Kf("Stunned"))
            .C("DodgeF", Kf("DashForward")).C("DodgeB", Kf("DashBackward")).C("DodgeL", Kf("DashLeft")).C("DodgeR", Kf("DashRight"))
            .C("Jump", Kf("Jump")).C("JumpAttack", Kf("Jump-Attack1"))
            .C("Death", Kf("Death")));

            return list;
        }

        // ---------------------------------------------------------------- menu

        [MenuItem("ArvinCombat/Setup Project (build everything)", priority = 0)]
        public static void SetupAll()
        {
            try
            {
                EditorUtility.DisplayProgressBar("ArvinCombat", "Configuring imports...", 0.1f);
                EnsureFolders();
                ConfigureImports();
                EditorUtility.DisplayProgressBar("ArvinCombat", "Building warriors...", 0.5f);
                BuildWarriors();
                EditorUtility.DisplayProgressBar("ArvinCombat", "Building scene...", 0.8f);
                ConfigurePlayer();
                BuildScene();
                Debug.Log("<b>ArvinCombat</b>: setup complete. Press Play in Assets/ArvinCombat/Scenes/Main.unity");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        [MenuItem("ArvinCombat/Rebuild Warriors (animators + prefabs)", priority = 1)]
        public static void RebuildWarriorsMenu()
        {
            EnsureFolders();
            BuildWarriors();
        }

        [MenuItem("ArvinCombat/Rebuild Arena Scene", priority = 2)]
        public static void RebuildSceneMenu()
        {
            EnsureFolders();
            BuildScene();
        }

        [MenuItem("ArvinCombat/Build for Web (WebGL)", priority = 10)]
        public static void BuildWeb()
        {
            if (!File.Exists(ScenePath)) SetupAll();
            EnsureConfig();
            ConfigurePlayer();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = WebBuildDir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;
            if (sum.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"<b>ArvinCombat</b>: web build OK ({sum.totalSize / (1024f * 1024f):0.0} MB) -> {Path.GetFullPath(WebBuildDir)}. Online server: {ServerBaseFromScene()}");
                // ZIP for upload in Arvin Games Studio
                string zip = Path.GetFullPath("Builds/ArvinCombat-WebGL.zip");
                try
                {
                    if (File.Exists(zip)) File.Delete(zip);
                    System.IO.Compression.ZipFile.CreateFromDirectory(Path.GetFullPath(WebBuildDir), zip);
                    Debug.Log("<b>ArvinCombat</b>: Studio upload ZIP -> " + zip);
                    EditorUtility.RevealInFinder(zip);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("ArvinCombat: could not create ZIP (" + e.Message + "). Zip the Builds/WebGL folder by hand.");
                    EditorUtility.RevealInFinder(Path.GetFullPath(WebBuildDir));
                }
            }
            else Debug.LogError("ArvinCombat: web build failed: " + sum.result);
        }

        [MenuItem("ArvinCombat/Build Room Server (Linux Dedicated Server)", priority = 11)]
        public static void BuildRoomServer()
        {
            if (!File.Exists(ScenePath)) SetupAll();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = RoomBuildPath,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;
            if (sum.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"<b>ArvinCombat</b>: room server build OK -> {Path.GetFullPath(RoomBuildPath)}");
                EditorUtility.RevealInFinder(Path.GetFullPath(RoomBuildPath));
            }
            else Debug.LogError("ArvinCombat: room server build failed: " + sum.result + " (install 'Linux Dedicated Server Build Support' in Unity Hub if missing)");
        }

        const string ConfigPath = "Assets/StreamingAssets/arvincombat.json";

        static string ServerBaseFromScene()
        {
            EnsureConfig();
            var cfg = new OnlineConfig();
            try { JsonUtility.FromJsonOverwrite(File.ReadAllText(ConfigPath), cfg); } catch { }
            return cfg.serverUrl;
        }

        static void EnsureConfig()
        {
            if (File.Exists(ConfigPath)) return;
            Directory.CreateDirectory("Assets/StreamingAssets");
            File.WriteAllText(ConfigPath, "{\n  \"serverUrl\": \"https://mm3.arvinplay.com\",\n  \"matchPath\": \"match\",\n  \"roomPath\": \"room\"\n}\n");
            AssetDatabase.ImportAsset(ConfigPath);
        }

        [MenuItem("ArvinCombat/Open Main Scene", priority = 20)]
        public static void OpenMainScene()
        {
            if (File.Exists(ScenePath)) EditorSceneManager.OpenScene(ScenePath);
        }

        static void EnsureFolders()
        {
            foreach (var p in new[] { Root, Gen, Gen + "/Controllers", Gen + "/Prefabs", Gen + "/Materials", Root + "/Resources", ResDir, Root + "/Scenes" })
            {
                if (AssetDatabase.IsValidFolder(p)) continue;
                var parent = Path.GetDirectoryName(p).Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, Path.GetFileName(p));
            }
        }

        // ---------------------------------------------------------------- imports

        static void ConfigureImports()
        {
            // which clips must loop?
            var loopByPath = new Dictionary<string, bool>();
            foreach (var cfg in Configs())
                foreach (var kv in cfg.clips)
                {
                    var path = FirstExisting(kv.Value);
                    if (path == null) continue;
                    bool loop = LoopKeys.Contains(kv.Key);
                    if (!loopByPath.ContainsKey(path)) loopByPath[path] = loop;
                    else loopByPath[path] |= loop;
                }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kv in loopByPath)
                {
                    if (kv.Key.EndsWith(".anim")) SetAnimLoop(kv.Key, kv.Value);
                    else SetModelLoop(kv.Key, kv.Value);
                }

                // character meshes: compress for web
                foreach (var cfg in Configs())
                {
                    foreach (var m in new[] { cfg.model, cfg.weaponModel })
                    {
                        if (string.IsNullOrEmpty(m)) continue;
                        var mi = AssetImporter.GetAtPath(m) as ModelImporter;
                        if (mi == null) continue;
                        if (mi.meshCompression != ModelImporterMeshCompression.Medium)
                        {
                            mi.meshCompression = ModelImporterMeshCompression.Medium;
                            mi.SaveAndReimport();
                        }
                    }
                }

                // textures: small and crunched on WebGL
                var texGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/ExplosiveLLC", "Assets/ARPGWarrior", "Assets/SamuraiAnimsetPro", "Assets/Fantacode Studios" });
                foreach (var g in texGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(g);
                    var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (ti == null) continue;
                    bool normal = ti.textureType == TextureImporterType.NormalMap || path.ToLower().Contains("normal");
                    int size = normal ? 512 : 1024;
                    var ps = ti.GetPlatformTextureSettings("WebGL");
                    if (ps.overridden && ps.maxTextureSize == size && ps.crunchedCompression) continue;
                    ps.overridden = true;
                    ps.maxTextureSize = size;
                    ps.crunchedCompression = true;
                    ps.compressionQuality = 50;
                    ps.textureCompression = TextureImporterCompression.Compressed;
                    ti.SetPlatformTextureSettings(ps);
                    ti.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();
        }

        static void SetModelLoop(string path, bool loop)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) return;
            var clips = mi.clipAnimations;
            if (clips == null || clips.Length == 0) clips = mi.defaultClipAnimations;
            if (clips == null || clips.Length == 0) return;
            bool changed = false;
            foreach (var c in clips)
            {
                if (c.loopTime != loop) { c.loopTime = loop; changed = true; }
                if (loop && !c.loopPose) { c.loopPose = true; changed = true; }
            }
            if (mi.animationCompression != ModelImporterAnimationCompression.Optimal)
            {
                mi.animationCompression = ModelImporterAnimationCompression.Optimal;
                changed = true;
            }
            if (!changed) return;
            mi.clipAnimations = clips;
            mi.SaveAndReimport();
        }

        static void SetAnimLoop(string path, bool loop)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null) return;
            var s = AnimationUtility.GetAnimationClipSettings(clip);
            if (s.loopTime == loop) return;
            s.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, s);
            EditorUtility.SetDirty(clip);
        }

        // ---------------------------------------------------------------- warriors

        static string FirstExisting(string[] paths)
        {
            foreach (var p in paths)
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }

        static AnimationClip LoadClip(string path)
        {
            if (path.EndsWith(".anim")) return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        }

        static void BuildWarriors()
        {
            AssetDatabase.SaveAssets();
            var registry = AssetDatabase.LoadAssetAtPath<WarriorRegistry>(RegistryPath);
            if (registry == null)
            {
                registry = ScriptableObject.CreateInstance<WarriorRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }
            registry.warriors.Clear();

            foreach (var cfg in Configs())
            {
                var clips = new Dictionary<string, AnimationClip>();
                foreach (var kv in cfg.clips)
                {
                    var path = FirstExisting(kv.Value);
                    if (path == null) { Debug.LogWarning($"ArvinCombat: {cfg.id} has no clip for '{kv.Key}' (looked for {string.Join(", ", kv.Value)})"); continue; }
                    var clip = LoadClip(path);
                    if (clip == null) { Debug.LogWarning($"ArvinCombat: no AnimationClip inside {path}"); continue; }
                    clips[kv.Key] = clip;
                }

                var ctrl = BuildController(cfg.id, clips);
                var prefab = BuildPrefab(cfg, ctrl);

                var vis = new WarriorVisual { id = cfg.id, prefab = prefab, modelScale = cfg.scale };
                foreach (var kv in clips)
                {
                    vis.clipKeys.Add(kv.Key);
                    vis.clipLengths.Add(kv.Value.length);
                }
                registry.warriors.Add(vis);
            }

            registry.setupVersion = SetupVersion;
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
            Debug.Log($"ArvinCombat: built {registry.warriors.Count} warriors.");
        }

        static AnimatorController BuildController(WarriorId id, Dictionary<string, AnimationClip> clips)
        {
            string path = $"{Gen}/Controllers/{id}.controller";
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
            ctrl.AddParameter("MoveX", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
            ctrl.AddParameter(new AnimatorControllerParameter { name = "MoveSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });
            ctrl.AddParameter(new AnimatorControllerParameter { name = "ActionSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f });

            var sm = ctrl.layers[0].stateMachine;

            var loco = ctrl.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = "MoveX";
            tree.blendParameterY = "MoveZ";
            tree.useAutomaticThresholds = false;
            AddChild(tree, clips, "Idle", Vector2.zero);
            AddChild(tree, clips, "WalkF", new Vector2(0, 1));
            AddChild(tree, clips, "WalkB", new Vector2(0, -1));
            AddChild(tree, clips, "WalkL", new Vector2(-1, 0));
            AddChild(tree, clips, "WalkR", new Vector2(1, 0));
            AddChild(tree, clips, "Run", new Vector2(0, 2));
            loco.speedParameterActive = true;
            loco.speedParameter = "MoveSpeed";
            sm.defaultState = loco;

            int i = 0;
            foreach (var kv in clips)
            {
                if (LocoKeys.Contains(kv.Key)) continue;
                var st = sm.AddState(kv.Key, new Vector3(500, -200 + 60 * i++, 0));
                st.motion = kv.Value;
                st.speedParameterActive = true;
                st.speedParameter = "ActionSpeed";
                st.writeDefaultValues = true;
            }

            EditorUtility.SetDirty(ctrl);
            return ctrl;
        }

        static void AddChild(BlendTree tree, Dictionary<string, AnimationClip> clips, string key, Vector2 pos)
        {
            if (clips.TryGetValue(key, out var c)) tree.AddChild(c, pos);
            else if (key != "Idle" && clips.TryGetValue("Idle", out var idle)) tree.AddChild(idle, pos);
        }

        static GameObject BuildPrefab(WarriorCfg cfg, AnimatorController ctrl)
        {
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(cfg.model);
            if (modelAsset == null) { Debug.LogError("ArvinCombat: model not found " + cfg.model); return null; }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = cfg.id + "_Model";
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one * cfg.scale;

            var animator = go.GetComponent<Animator>();
            if (animator == null) animator = go.AddComponent<Animator>();
            if (animator.avatar == null)
                animator.avatar = AssetDatabase.LoadAllAssetsAtPath(cfg.model).OfType<Avatar>().FirstOrDefault();
            animator.runtimeAnimatorController = ctrl;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // materials
            Material force = string.IsNullOrEmpty(cfg.forceMaterial) ? null : AssetDatabase.LoadAssetAtPath<Material>(cfg.forceMaterial);
            Material fallback = string.IsNullOrEmpty(cfg.fallbackMaterial) ? null : AssetDatabase.LoadAssetAtPath<Material>(cfg.fallbackMaterial);
            var tintCache = new Dictionary<Material, Material>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (force != null) mats[m] = force;
                    else if (fallback != null && (mats[m] == null || mats[m].name.StartsWith("Default") || mats[m].name.StartsWith("No Name")))
                        mats[m] = fallback;
                    if (cfg.tint != Color.white && mats[m] != null) mats[m] = Tinted(mats[m], cfg.tint, cfg.id, tintCache);
                }
                r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.On;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }

            // weapon
            if (!string.IsNullOrEmpty(cfg.weaponModel))
            {
                var wAsset = AssetDatabase.LoadAssetAtPath<GameObject>(cfg.weaponModel);
                Transform bone = FindDeep(go.transform, cfg.weaponBone);
                if (bone == null && animator.isHuman) bone = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (wAsset != null && bone != null)
                {
                    var w = (GameObject)PrefabUtility.InstantiatePrefab(wAsset);
                    PrefabUtility.UnpackPrefabInstance(w, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    w.name = "Weapon";
                    w.transform.SetParent(bone, false);
                    w.transform.localPosition = cfg.weaponPos;
                    w.transform.localRotation = Quaternion.Euler(cfg.weaponEuler);
                    w.transform.localScale = Vector3.one;
                    var wm = string.IsNullOrEmpty(cfg.weaponMaterial) ? null : AssetDatabase.LoadAssetAtPath<Material>(cfg.weaponMaterial);
                    foreach (var r in w.GetComponentsInChildren<Renderer>(true))
                    {
                        var mats = r.sharedMaterials;
                        for (int m = 0; m < mats.Length; m++)
                            if (wm != null && (mats[m] == null || mats[m].name.StartsWith("Default") || mats[m].name.StartsWith("No Name"))) mats[m] = wm;
                        r.sharedMaterials = mats;
                    }
                }
                else Debug.LogWarning($"ArvinCombat: could not attach weapon for {cfg.id} (bone '{cfg.weaponBone}' found: {bone != null})");
            }

            string path = $"{Gen}/Prefabs/{cfg.id}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        static Material Tinted(Material src, Color tint, WarriorId id, Dictionary<Material, Material> cache)
        {
            if (cache.TryGetValue(src, out var done)) return done;
            string path = $"{Gen}/Materials/{id}_{src.name}.mat";
            var m = new Material(src);
            if (m.HasProperty("_Color")) m.color = src.color * tint;
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(m, path);
            cache[src] = m;
            return m;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase)) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        // ---------------------------------------------------------------- scene

        static Material Mat(string name, Color c, float smooth = 0.2f, float metal = 0f, Color? emission = null)
        {
            string path = $"{Gen}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.color = c;
            m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", metal);
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission.Value);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.Euler(euler);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Color fog = new Color(0.10f, 0.11f, 0.15f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 18f;
            RenderSettings.fogEndDistance = 60f;
            RenderSettings.fogColor = fog;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.45f, 0.48f, 0.58f);
            RenderSettings.ambientEquatorColor = new Color(0.30f, 0.28f, 0.28f);
            RenderSettings.ambientGroundColor = new Color(0.12f, 0.10f, 0.09f);
            RenderSettings.skybox = null;

            // light
            var sun = new GameObject("Sun");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.92f, 0.82f);
            light.intensity = 1.25f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.75f;
            sun.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            // camera
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = fog;
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 80f;
            camGo.AddComponent<AudioListener>();
            var fightCam = camGo.AddComponent<FightCamera>();
            camGo.transform.position = new Vector3(0, 5, -12);
            camGo.transform.LookAt(Vector3.up);

            // arena (primitives only = tiny download)
            var arena = new GameObject("Arena").transform;
            var stone = Mat("Arena_Stone", new Color(0.42f, 0.40f, 0.37f), 0.15f);
            var stoneDark = Mat("Arena_StoneDark", new Color(0.24f, 0.22f, 0.21f), 0.1f);
            var sand = Mat("Arena_Sand", new Color(0.58f, 0.50f, 0.38f), 0.05f);
            var ground = Mat("Arena_Ground", new Color(0.13f, 0.14f, 0.12f), 0.05f);
            var accent = Mat("Arena_Banner", new Color(0.55f, 0.08f, 0.06f), 0.2f);
            var fire = Mat("Arena_Fire", new Color(1f, 0.6f, 0.2f), 0.5f, 0f, new Color(1f, 0.45f, 0.1f) * 2.5f);

            Prim(PrimitiveType.Plane, "Ground", arena, new Vector3(0, -0.35f, 0), new Vector3(12, 1, 12), ground);
            Prim(PrimitiveType.Cylinder, "Platform", arena, new Vector3(0, -0.2f, 0), new Vector3(22f, 0.2f, 22f), stoneDark);
            Prim(PrimitiveType.Cylinder, "FightFloor", arena, new Vector3(0, -0.19f, 0), new Vector3(20.4f, 0.2f, 20.4f), sand);
            Prim(PrimitiveType.Cylinder, "CenterMark", arena, new Vector3(0, 0.002f, 0), new Vector3(3f, 0.001f, 3f), stone);

            int segments = 40;
            float radius = 10.8f;
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                var pos = new Vector3(Mathf.Sin(a) * radius, 0.2f, Mathf.Cos(a) * radius);
                Prim(PrimitiveType.Cube, "Wall" + i, arena, pos, new Vector3(1.75f, 0.8f, 0.5f), stone, new Vector3(0, a * Mathf.Rad2Deg, 0));
            }
            for (int i = 0; i < 8; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / 8;
                var pos = new Vector3(Mathf.Sin(a) * 12f, 1.5f, Mathf.Cos(a) * 12f);
                Prim(PrimitiveType.Cylinder, "Pillar" + i, arena, pos, new Vector3(0.9f, 1.8f, 0.9f), stoneDark);
                Prim(PrimitiveType.Cube, "Fire" + i, arena, pos + Vector3.up * 1.95f, new Vector3(0.5f, 0.3f, 0.5f), fire, new Vector3(0, 45, 0));
                Prim(PrimitiveType.Cube, "Banner" + i, arena, pos + new Vector3(0, 0.3f, 0) - pos.normalized * 0.46f, new Vector3(0.6f, 2.2f, 0.04f), accent, new Vector3(0, a * Mathf.Rad2Deg, 0));
            }

            // game objects
            var game = new GameObject("Game");
            var gm = game.AddComponent<GameManager>();
            var mm = game.AddComponent<MatchManager>();
            var ui = game.AddComponent<GameUI>();
            var fxGo = new GameObject("HitFx");
            fxGo.transform.SetParent(game.transform, false);
            var fx = fxGo.AddComponent<HitFx>();
            var om = game.AddComponent<OnlineMatch>();
            gm.match = mm; gm.ui = ui; gm.fightCamera = fightCam; gm.online = om;
            mm.cam = fightCam; mm.fx = fx; mm.ui = ui;
            om.cam = fightCam; om.fx = fx; om.ui = ui;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- player settings

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Arvin Games";
            PlayerSettings.productName = "Arvin Combat";
            PlayerSettings.runInBackground = true;
            PlayerSettings.stripEngineCode = true;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Medium);
            // gzip works over plain http and https; the ArvinCombat server sends the right headers
            // gzip + decompression fallback: loads on any host / CDN even without Content-Encoding headers
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.template = "PROJECT:ArvinCombat";
            PlayerSettings.WebGL.nameFilesAsHashes = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            QualitySettings.shadowDistance = 30f;
        }
    }

    /// <summary>Runs the setup automatically the first time the project is opened.</summary>
    [InitializeOnLoad]
    static class ArvinCombatAutoSetup
    {
        static ArvinCombatAutoSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (SessionState.GetBool("ArvinCombat.AutoSetupDone", false)) return;
                var reg = AssetDatabase.LoadAssetAtPath<WarriorRegistry>("Assets/ArvinCombat/Resources/ArvinCombat/WarriorRegistry.asset");
                if (reg != null && reg.setupVersion >= ArvinCombatSetup.SetupVersion) return;
                SessionState.SetBool("ArvinCombat.AutoSetupDone", true);
                Debug.Log(reg == null ? "ArvinCombat: first open - running automatic setup..." : "ArvinCombat: setup changed - rebuilding warriors...");
                ArvinCombatSetup.SetupAll();
                ArvinCombatSetup.OpenMainScene();
            };
        }
    }
}
