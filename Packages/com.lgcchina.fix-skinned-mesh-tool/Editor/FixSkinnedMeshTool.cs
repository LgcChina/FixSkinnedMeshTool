#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.Text;

namespace LGC.Tools
{
    /// <summary>
    /// 极简版蒙皮网格修复工具 | LGC菜单 | 低版本兼容 | 解决网格不显示+根骨骼丢失
    /// 核心：槽2参考→槽1根节点全域找骨骼 | 根骨骼强制兜底 | 网格渲染多重保障
    /// </summary>
    public class FixSkinnedMeshTool : EditorWindow
    {
        private GameObject _targetObj;  // 槽1：待修复失效蒙皮物体
        private GameObject _sourceObj;  // 槽2：原版FBX参考物体

        // 缓存骨骼映射，避免重复查找
        private Dictionary<string, Transform> _boneMapCache;

        // 统计信息
        private int _matchedBones = 0;
        private int _totalBones = 0;

        // 内联提示
        private string _tipText;
        private MessageType _tipType = MessageType.None;

        // 窗口尺寸可调整
        private const float MIN_WINDOW_WIDTH = 420f;
        private const float MIN_WINDOW_HEIGHT = 320f;
        private const float MAX_WINDOW_WIDTH = 800f;
        private const float MAX_WINDOW_HEIGHT = 600f;

        [MenuItem("LGC/修复蒙皮网格引用", false, 10)]
        public static void OpenTool()
        {
            var window = GetWindow<FixSkinnedMeshTool>("修复蒙皮网格");
            window.minSize = new Vector2(MIN_WINDOW_WIDTH, MIN_WINDOW_HEIGHT);
            window.maxSize = new Vector2(MAX_WINDOW_WIDTH, MAX_WINDOW_HEIGHT);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(5);

            // 标题区域
            EditorGUILayout.LabelField("LGC 蒙皮网格修复工具", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("基于槽2参考，从槽1模型根节点全域查找匹配骨骼", MessageType.Info);

            EditorGUILayout.Space(10);

            // 槽位选择区域
            EditorGUILayout.BeginVertical(GUI.skin.box);
            {
                EditorGUILayout.LabelField("修复对象选择", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);

                _targetObj = EditorGUILayout.ObjectField("【槽1】失效蒙皮物体", _targetObj, typeof(GameObject), true) as GameObject;
                _sourceObj = EditorGUILayout.ObjectField("【槽2】参考FBX物体", _sourceObj, typeof(GameObject), true) as GameObject;
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // 对象信息预览
            if (_targetObj != null || _sourceObj != null)
            {
                EditorGUILayout.BeginVertical(GUI.skin.box);
                {
                    EditorGUILayout.LabelField("对象信息", EditorStyles.boldLabel);

                    if (_targetObj != null)
                    {
                        var targetRenderer = _targetObj.GetComponent<SkinnedMeshRenderer>();
                        EditorGUILayout.LabelField($"目标对象: {_targetObj.name}",
                            targetRenderer != null ? "✓ 有蒙皮组件" : "✗ 无蒙皮组件");
                    }

                    if (_sourceObj != null)
                    {
                        var sourceRenderer = _sourceObj.GetComponent<SkinnedMeshRenderer>();
                        var hasMesh = sourceRenderer != null && sourceRenderer.sharedMesh != null;
                        EditorGUILayout.LabelField($"参考对象: {_sourceObj.name}",
                            hasMesh ? "✓ 有蒙皮网格" : "✗ 无有效网格");
                    }
                }
                EditorGUILayout.EndVertical();

                EditorGUILayout.Space(10);
            }

            // 提示信息区域
            if (!string.IsNullOrEmpty(_tipText))
            {
                EditorGUILayout.HelpBox(_tipText, _tipType);
                EditorGUILayout.Space(5);
            }

            // 操作按钮区域
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.enabled = CanFix();
            if (GUILayout.Button("执行修复", GUILayout.Width(120), GUILayout.Height(35)))
            {
                FixCoreLogic();
            }
            GUI.enabled = true;

            if (GUILayout.Button("分析匹配", GUILayout.Width(80), GUILayout.Height(35)))
            {
                AnalyzeBoneMatch();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
        }

        #region 核心校验
        private bool CanFix()
        {
            if (_targetObj == null || _sourceObj == null)
                return false;

            SkinnedMeshRenderer targetMr = _targetObj.GetComponent<SkinnedMeshRenderer>();
            SkinnedMeshRenderer sourceMr = _sourceObj.GetComponent<SkinnedMeshRenderer>();

            return targetMr != null && sourceMr != null && sourceMr.sharedMesh != null;
        }
        #endregion

        #region 核心修复逻辑
        private void FixCoreLogic()
        {
            ClearTip();

            SkinnedMeshRenderer targetMr = _targetObj.GetComponent<SkinnedMeshRenderer>();
            SkinnedMeshRenderer sourceMr = _sourceObj.GetComponent<SkinnedMeshRenderer>();
            Transform targetRoot = _targetObj.transform.root;

            try
            {
                // 1. 记录Undo操作
                Undo.RecordObject(targetMr, "LGC蒙皮修复");

                // 2. 构建骨骼映射缓存
                BuildBoneMapCache(targetRoot);

                // 3. 同步网格、材质和边界
                SyncMeshMaterialsAndBounds(targetMr, sourceMr);

                // 4. 重构骨骼数组
                Transform[] newBones = RebuildBonesArray(sourceMr.bones);
                List<string> lostBones = GetLostBones(newBones, sourceMr.bones);
                targetMr.bones = newBones;

                // 5. 设置根骨骼
                SetRootBone(targetMr, sourceMr, targetRoot);

                // 6. 刷新渲染器
                RefreshRenderer(targetMr);

                // 7. 显示结果
                ShowResult(lostBones);
            }
            catch (System.Exception e)
            {
                _tipText = $"修复失败: {e.Message}";
                _tipType = MessageType.Error;
                Debug.LogError($"[LGC蒙皮工具] 修复失败: {e.Message}\n{e.StackTrace}");
            }
        }

        private void ClearTip()
        {
            _tipText = "";
            _tipType = MessageType.None;
            _matchedBones = 0;
            _totalBones = 0;
            Repaint();
        }

        private void BuildBoneMapCache(Transform root)
        {
            _boneMapCache = new Dictionary<string, Transform>();
            BuildBoneMapRecursive(root, _boneMapCache);
        }

        private void BuildBoneMapRecursive(Transform node, Dictionary<string, Transform> boneMap)
        {
            if (node == null) return;

            // 避免重复名称，优先使用第一个找到的
            if (!boneMap.ContainsKey(node.name))
                boneMap[node.name] = node;

            for (int i = 0; i < node.childCount; i++)
                BuildBoneMapRecursive(node.GetChild(i), boneMap);
        }

        private void SyncMeshMaterialsAndBounds(SkinnedMeshRenderer target, SkinnedMeshRenderer source)
        {
            // 强制同步网格
            target.sharedMesh = source.sharedMesh;

            // 材质同步
            Material[] sourceMats = source.sharedMaterials;
            Material[] newMats = new Material[sourceMats.Length];

            for (int i = 0; i < sourceMats.Length; i++)
                newMats[i] = sourceMats[i] ?? CreateDefaultMaterial();

            target.sharedMaterials = newMats;

            // 同步边界
            target.localBounds = source.localBounds;
        }

        private Transform[] RebuildBonesArray(Transform[] sourceBones)
        {
            if (sourceBones == null)
                return new Transform[0];

            Transform[] newBones = new Transform[sourceBones.Length];
            _totalBones = sourceBones.Length;
            _matchedBones = 0;

            for (int i = 0; i < sourceBones.Length; i++)
            {
                if (sourceBones[i] == null)
                    continue;

                string boneName = sourceBones[i].name;
                if (_boneMapCache.TryGetValue(boneName, out Transform matchedBone))
                {
                    newBones[i] = matchedBone;
                    _matchedBones++;
                }
            }

            return newBones;
        }

        private List<string> GetLostBones(Transform[] newBones, Transform[] sourceBones)
        {
            List<string> lost = new List<string>();

            if (sourceBones == null)
                return lost;

            for (int i = 0; i < newBones.Length; i++)
            {
                if (newBones[i] == null && sourceBones[i] != null)
                    lost.Add(sourceBones[i].name);
            }

            return lost;
        }

        private void SetRootBone(SkinnedMeshRenderer target, SkinnedMeshRenderer source, Transform targetRoot)
        {
            Transform newRootBone = null;

            // 尝试按名称查找根骨骼
            if (source.rootBone != null)
            {
                string rootName = source.rootBone.name;
                if (!string.IsNullOrEmpty(rootName))
                    _boneMapCache.TryGetValue(rootName, out newRootBone);
            }

            // 兜底策略
            if (newRootBone == null)
            {
                // 尝试常见根骨骼名称
                string[] commonRootNames = { "Hips", "Bip001", "Bip01", "Root", "Pelvis", "Armature" };
                foreach (string name in commonRootNames)
                {
                    if (_boneMapCache.TryGetValue(name, out newRootBone))
                        break;
                }

                // 最后使用模型根节点
                newRootBone = newRootBone ?? targetRoot;
            }

            target.rootBone = newRootBone;
        }

        private void RefreshRenderer(SkinnedMeshRenderer renderer)
        {
            renderer.updateWhenOffscreen = true;
            renderer.enabled = true;

            EditorApplication.delayCall += () =>
            {
                // 双重刷新确保显示
                renderer.enabled = false;
                renderer.enabled = true;

                SceneView.RepaintAll();
                EditorUtility.SetDirty(renderer);

                // 重新选择对象以刷新Inspector
                Selection.activeObject = null;
                EditorApplication.delayCall += () =>
                {
                    Selection.activeObject = _targetObj;
                };
            };
        }

        private void ShowResult(List<string> lostBones)
        {
            StringBuilder sb = new StringBuilder();

            // 骨骼匹配信息
            float matchRate = _totalBones > 0 ? (float)_matchedBones / _totalBones : 0f;
            sb.AppendLine($"骨骼匹配: {_matchedBones}/{_totalBones} ({matchRate:P0})");

            // 根骨骼信息
            var targetMr = _targetObj.GetComponent<SkinnedMeshRenderer>();
            sb.AppendLine($"根骨骼: {(targetMr.rootBone != null ? targetMr.rootBone.name : "未设置")}");

            // 缺失骨骼信息
            if (lostBones.Count > 0)
            {
                sb.AppendLine($"缺失骨骼: {lostBones.Count}个");
                if (lostBones.Count <= 5)
                    sb.Append(string.Join(", ", lostBones));
                else
                    sb.Append($"{string.Join(", ", lostBones.GetRange(0, 5))}...等{lostBones.Count}个");

                _tipType = MessageType.Warning;
            }
            else
            {
                sb.Append("✓ 所有骨骼已匹配");
                _tipType = MessageType.Info;
            }

            _tipText = sb.ToString();
        }

        private Material CreateDefaultMaterial()
        {
            return new Material(Shader.Find("Standard"));
        }
        #endregion

        #region 分析功能
        private void AnalyzeBoneMatch()
        {
            if (!CanFix())
            {
                _tipText = "请先选择两个有效的蒙皮物体";
                _tipType = MessageType.Error;
                return;
            }

            ClearTip();

            var sourceMr = _sourceObj.GetComponent<SkinnedMeshRenderer>();
            var targetRoot = _targetObj.transform.root;

            BuildBoneMapCache(targetRoot);

            if (sourceMr.bones == null || sourceMr.bones.Length == 0)
            {
                _tipText = "参考对象没有骨骼数据";
                _tipType = MessageType.Warning;
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("骨骼匹配分析:");
            sb.AppendLine($"目标模型总骨骼数: {_boneMapCache.Count}");
            sb.AppendLine($"参考模型蒙皮骨骼数: {sourceMr.bones.Length}");

            int matched = 0;
            List<string> unmatched = new List<string>();

            foreach (var bone in sourceMr.bones)
            {
                if (bone != null)
                {
                    if (_boneMapCache.ContainsKey(bone.name))
                        matched++;
                    else
                        unmatched.Add(bone.name);
                }
            }

            float rate = sourceMr.bones.Length > 0 ? (float)matched / sourceMr.bones.Length : 0f;
            sb.AppendLine($"可匹配骨骼: {matched}/{sourceMr.bones.Length} ({rate:P0})");

            if (unmatched.Count > 0)
            {
                sb.AppendLine("未匹配骨骼:");
                for (int i = 0; i < Mathf.Min(unmatched.Count, 10); i++)
                    sb.AppendLine($"  - {unmatched[i]}");

                if (unmatched.Count > 10)
                    sb.AppendLine($"  ...等{unmatched.Count}个");
            }

            _tipText = sb.ToString();
            _tipType = rate > 0.8f ? MessageType.Info : MessageType.Warning;
        }
        #endregion
    }
}
#endif
