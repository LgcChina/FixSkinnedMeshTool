#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class FixSkinnedMeshBoneTool : EditorWindow
{
    private GameObject damagedModel;
    private GameObject sourceFBX; // 仅接受Project窗口的FBX资产（支持拖入FBX子节点 -> 自动提升为根资产）
    private Transform customRootBone;

    // 显示控制
    private bool showAllMeshObjects = false;

    // 记录刚修复的网格物体全路径（用于加粗显示）
    private HashSet<string> recentlyFixedMeshPaths = new HashSet<string>();

    // 缓存损坏模型路径字典（用于判断是否缺失）
    private Dictionary<string, Transform> cachedDamagedPathDict;

    // 缺失骨骼相关
    private List<BoneNode> missingBonesTree = new List<BoneNode>();
    private Vector2 boneScrollPos;

    // 网格物体相关
    private List<BoneNode> missingMeshObjectsTree = new List<BoneNode>();
    private List<BoneNode> allMeshObjectsTree = new List<BoneNode>();
    private Vector2 meshScrollPos;

    // 蒙皮修复缓存与统计
    private Dictionary<string, Transform> _boneMapCache;
    private int _matchedBones = 0;
    private int _totalBones = 0;

    private string infoMessage = "";
    private MessageType infoMessageType = MessageType.Info;

    // 若用户拖了 FBX 子节点，这里保存其在 FBX 内的“可能路径/名称”提示
    private string _sourceMeshPathHint = "";
    private bool _sourceFromSubObject = false; // 用于 UI 提示

    // ==== 临时源实例管理（双通道） ====
    // 记录用 LoadPrefabContents 打开的根，用于 Cleanup 时 Unload
    private HashSet<GameObject> _prefabContentsRoots = new HashSet<GameObject>();
    // 记录用 Preview Scene 实例化的根，以及它所在的预览场景，用于 Cleanup 时销毁与关闭
    private Dictionary<GameObject, Scene> _previewSceneRoots = new Dictionary<GameObject, Scene>();

    // 窗口菜单与标题
    [MenuItem("LGC/LGC 蒙皮网格骨骼修复工具")]
    public static void ShowWindow()
    {
        GetWindow<FixSkinnedMeshBoneTool>("LGC 蒙皮网格骨骼修复工具");
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("将损坏的人物拖入槽位1，原始 FBX 资产拖入槽位2", EditorStyles.wordWrappedLabel);
        EditorGUILayout.Space();

        // 槽位 1：损坏对象
        damagedModel = EditorGUILayout.ObjectField("损坏人物 (场景)", damagedModel, typeof(GameObject), true) as GameObject;

        // 槽位 2：原始 FBX（支持拖入子节点或场景实例，内部会自动规范为 FBX 根资产）
        var newSource = EditorGUILayout.ObjectField("原始模型 (FBX资产)", sourceFBX, typeof(GameObject), false) as GameObject;
        if (newSource != sourceFBX)
        {
            sourceFBX = newSource;
            NormalizeSourceFBXSelection(); // 自动提升为 FBX 根，并尝试生成子节点路径/名称提示
        }

        // 可选根骨
        EditorGUILayout.LabelField("骨骼根节点 (可选，覆盖自动识别)", EditorStyles.wordWrappedLabel);
        customRootBone = EditorGUILayout.ObjectField(customRootBone, typeof(Transform), true) as Transform;

        EditorGUILayout.Space();

        // 提示信息区域
        if (!string.IsNullOrEmpty(infoMessage))
        {
            EditorGUILayout.HelpBox(infoMessage, infoMessageType);
        }

        // 当损坏对象像“单个网格子物体”时的说明（非阻塞）
        if (damagedModel != null && LooksLikeSingleMeshObject(damagedModel))
        {
            EditorGUILayout.HelpBox(
                "检测到你拖入的是单个蒙皮网格。点击下方按钮将自动按“单网格修复”处理，避免整人物体检与多余的 FBX 实例。",
                MessageType.Warning
            );
        }

        // 当用户在 FBX 槽位拖的是 FBX 子节点或场景实例时的说明（非阻塞）
        if (_sourceFromSubObject && sourceFBX != null)
        {
            EditorGUILayout.HelpBox(
                "你拖入的是 FBX 的子节点或场景中的实例。工具已自动提升为该 FBX 根资产，" +
                "并会优先按子节点的名称/路径进行匹配，不会在场景中创建额外的 FBX 实例。",
                MessageType.Warning
            );
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("显示所有网格物体（不仅缺失）", GUILayout.Width(200));
        showAllMeshObjects = EditorGUILayout.Toggle(showAllMeshObjects);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // 检查 / 自动分流按钮
        bool canCompare = damagedModel != null && sourceFBX != null;
        EditorGUI.BeginDisabledGroup(!canCompare);
        if (GUILayout.Button("检查骨骼与网格", GUILayout.Height(30)))
        {
            // 若槽位1看起来是单个网格物体，自动走单网格修复；否则执行整人物体检
            if (LooksLikeSingleMeshObject(damagedModel))
                FixSingleMeshFromDamagedSlot();
            else
                CheckBonesAndMeshes();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space();

        // ========== 缺失骨骼列表区域 ==========
        EditorGUILayout.LabelField("缺失骨骼列表", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        if (missingBonesTree.Count > 0)
        {
            boneScrollPos = EditorGUILayout.BeginScrollView(boneScrollPos, GUILayout.MaxHeight(250));
            DrawBoneTree(missingBonesTree, 0);
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.LabelField("暂无缺失骨骼数据，点击上方检查按钮开始检测", EditorStyles.centeredGreyMiniLabel);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider); // 分割线
        EditorGUILayout.Space();

        // ========== 网格物体列表区域 ==========
        // 可选：简要图例
        EditorGUILayout.LabelField(showAllMeshObjects ? "所有网格物体列表" : "缺失/无效网格物体列表", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("红色 = 路径缺失；黄色 = 无效蒙皮网格（无SMR/mesh为空/骨骼为空）", EditorStyles.miniLabel);
        EditorGUILayout.Space();

        bool hasMeshData = (showAllMeshObjects && allMeshObjectsTree.Count > 0)
                           || (!showAllMeshObjects && missingMeshObjectsTree.Count > 0);

        if (hasMeshData)
        {
            meshScrollPos = EditorGUILayout.BeginScrollView(meshScrollPos, GUILayout.MaxHeight(250));
            if (showAllMeshObjects)
            {
                DrawMeshTree(allMeshObjectsTree, 0);
            }
            else
            {
                DrawMeshTree(missingMeshObjectsTree, 0);
            }
            EditorGUILayout.EndScrollView();
        }
        else
        {
            string emptyTip = showAllMeshObjects
                ? "暂无网格物体数据，点击上方检查按钮开始检测"
                : "暂无缺失/无效网格数据，点击上方检查按钮开始检测";
            EditorGUILayout.LabelField(emptyTip, EditorStyles.centeredGreyMiniLabel);
        }

        EditorGUILayout.Space();
    }

    // ---------- 规范化：把 FBX 槽位中的选择“提升”为 FBX 根资产，并尝试生成路径提示 ----------
    private void NormalizeSourceFBXSelection()
    {
        _sourceFromSubObject = false;
        _sourceMeshPathHint = "";

        if (sourceFBX == null) return;

        // 如果用户拖的是场景中的实例或 FBX 子节点，尝试定位其 FBX 资产
        string assetPath = AssetDatabase.GetAssetPath(sourceFBX);
        if (string.IsNullOrEmpty(assetPath))
        {
            // 非资产（大概率场景实例） -> 找到其对应 FBX 资产
            var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(sourceFBX);
            if (original != null)
            {
                assetPath = AssetDatabase.GetAssetPath(original);
                _sourceFromSubObject = true;
                _sourceMeshPathHint = sourceFBX.name; // 简易名称提示（路径精定位在加载 FBX 内容后再尝试）
            }
        }
        else
        {
            // 是资产。若拖的是 FBX 内子节点（不是根），也视为“子对象来源”
            if (PrefabUtility.IsPartOfPrefabAsset(sourceFBX) && sourceFBX.transform.parent != null)
            {
                _sourceFromSubObject = true;
                _sourceMeshPathHint = sourceFBX.name;
            }
        }

        if (!string.IsNullOrEmpty(assetPath))
        {
            // 以 FBX 根资产作为统一入口
            var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (fbxRoot != null)
            {
                sourceFBX = fbxRoot;
            }
        }
    }

    // ---------- 创建临时源实例（双通道：优先内存打开；失败则预览场景实例化） ----------
    private GameObject CreateTempSource()
    {
        if (sourceFBX == null)
        {
            SetMessage("源模型为空，无法创建临时实例", MessageType.Error);
            return null;
        }

        string assetPath = AssetDatabase.GetAssetPath(sourceFBX);
        if (string.IsNullOrEmpty(assetPath))
        {
            SetMessage("请拖入 Project 窗口中的 FBX 资产或其子节点/场景实例。", MessageType.Error);
            return null;
        }

        // 通道 A：LoadPrefabContents（不污染当前场景）
        try
        {
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root != null)
            {
                _prefabContentsRoots.Add(root);
                return root;
            }
        }
        catch
        {
            // fallthrough to 通道 B
        }

        // 通道 B：在 Preview Scene 里实例化，不污染当前场景层级
        try
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (src == null)
            {
                SetMessage("无法加载 FBX 资产。", MessageType.Error);
                return null;
            }

            // 创建预览场景
            var preview = EditorSceneManager.NewPreviewScene();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(src);
            if (instance == null)
            {
                EditorSceneManager.ClosePreviewScene(preview);
                SetMessage("实例化 FBX 失败。", MessageType.Error);
                return null;
            }

            // 移动到预览场景并隐藏
            SceneManager.MoveGameObjectToScene(instance, preview);
            instance.hideFlags = HideFlags.HideAndDontSave;

            _previewSceneRoots[instance] = preview;
            return instance;
        }
        catch (System.Exception e)
        {
            SetMessage($"加载 FBX 内容失败：{e.Message}", MessageType.Error);
            Debug.LogError($"加载 FBX 内容失败：{e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    // ---------- 统一清理临时源 ----------
    private void CleanupTempSource(GameObject temp)
    {
        if (temp == null) return;

        // 通道 A：LoadPrefabContents 打开的
        if (_prefabContentsRoots.Contains(temp))
        {
            PrefabUtility.UnloadPrefabContents(temp);
            _prefabContentsRoots.Remove(temp);
            return;
        }

        // 通道 B：Preview Scene 实例化的
        if (_previewSceneRoots.TryGetValue(temp, out var preview))
        {
            if (temp != null)
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
            if (preview.IsValid())
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
            _previewSceneRoots.Remove(temp);
            return;
        }

        // 兜底：如果两者都不是（理论上不会），直接销毁
        UnityEngine.Object.DestroyImmediate(temp);
    }

    // ---------- 快捷设置提示信息 ----------
    private void SetMessage(string text, MessageType type)
    {
        infoMessage = text;
        infoMessageType = type;
        Repaint();
    }

    // ---------- 清空提示信息 ----------
    private void ClearMessage()
    {
        infoMessage = "";
        infoMessageType = MessageType.Info;
        Repaint();
    }

    // ---------- 构建全路径字典 ----------
    private Dictionary<string, Transform> BuildFullPathDict(Transform root)
    {
        Dictionary<string, Transform> pathDict = new Dictionary<string, Transform>();
        pathDict[""] = root;
        foreach (Transform child in root)
        {
            BuildPathDictRecursive(child, child.name, pathDict);
        }
        return pathDict;
    }

    private void BuildPathDictRecursive(Transform node, string currentFullPath, Dictionary<string, Transform> dict)
    {
        dict[currentFullPath] = node;
        foreach (Transform child in node)
        {
            BuildPathDictRecursive(child, currentFullPath + "/" + child.name, dict);
        }
    }

    // ---------- 查找缺失骨骼 ----------
    private List<BoneNode> FindMissingBones(Transform sourceNode, Dictionary<string, Transform> damagedPathDict, string currentFullPath)
    {
        List<BoneNode> result = new List<BoneNode>();

        if (sourceNode.GetComponent<Renderer>() != null
            || sourceNode.GetComponent<SkinnedMeshRenderer>() != null)
        {
            return result;
        }

        bool isBoneMissing = !damagedPathDict.ContainsKey(currentFullPath);
        if (isBoneMissing)
        {
            BoneNode missingNode = new BoneNode
            {
                name = sourceNode.name,
                fullPath = currentFullPath,
                sourceTransform = sourceNode
            };
            foreach (Transform child in sourceNode)
            {
                string childFullPath = currentFullPath + "/" + child.name;
                missingNode.children.AddRange(FindMissingBones(child, damagedPathDict, childFullPath));
            }
            result.Add(missingNode);
        }
        else
        {
            foreach (Transform child in sourceNode)
            {
                string childFullPath = currentFullPath + "/" + child.name;
                result.AddRange(FindMissingBones(child, damagedPathDict, childFullPath));
            }
        }
        return result;
    }

    // ---------- 收集所有网格物体 ----------
    private void CollectAllMeshes(Transform sourceNode, Dictionary<string, Transform> damagedPathDict, string currentFullPath, List<BoneNode> meshTree)
    {
        if (sourceNode == null) return;

        bool isMeshObject = sourceNode.GetComponent<Renderer>() != null
                            || sourceNode.GetComponent<SkinnedMeshRenderer>() != null;

        if (isMeshObject)
        {
            BoneNode meshNode = new BoneNode
            {
                name = sourceNode.name,
                fullPath = currentFullPath,
                sourceTransform = sourceNode
            };
            foreach (Transform child in sourceNode)
            {
                string childFullPath = string.IsNullOrEmpty(currentFullPath) ? child.name : currentFullPath + "/" + child.name;
                CollectAllMeshes(child, damagedPathDict, childFullPath, meshNode.children);
            }
            meshTree.Add(meshNode);
        }
        else
        {
            foreach (Transform child in sourceNode)
            {
                string childFullPath = string.IsNullOrEmpty(currentFullPath) ? child.name : currentFullPath + "/" + child.name;
                CollectAllMeshes(child, damagedPathDict, childFullPath, meshTree);
            }
        }
    }

    // ---------- 自动识别骨骼根节点 ----------
    private Transform FindSkeletonRoot(Transform modelRoot)
    {
        Queue<Transform> searchQueue = new Queue<Transform>();
        searchQueue.Enqueue(modelRoot);

        while (searchQueue.Count > 0)
        {
            Transform current = searchQueue.Dequeue();
            string nodeNameLower = current.name.ToLower();
            if (nodeNameLower.Contains("armature")
                || nodeNameLower.Contains("hips")
                || nodeNameLower.Contains("rig"))
            {
                Debug.Log($"自动识别骨骼根节点：{current.name}");
                return current;
            }
            foreach (Transform child in current)
            {
                searchQueue.Enqueue(child);
            }
        }

        SetMessage("未自动识别到Armature/Hips骨骼根节点，将对比全模型层级", MessageType.Warning);
        Debug.LogWarning("未自动识别到骨骼根节点，可手动指定骨骼根节点优化结果");
        return modelRoot;
    }

    // ---------- 核心检查逻辑 ----------
    private void CheckBonesAndMeshes()
    {
        Debug.Log("===== 开始检查骨骼与网格 =====");
        ClearMessage();
        missingBonesTree.Clear();
        missingMeshObjectsTree.Clear();
        allMeshObjectsTree.Clear();

        GameObject tempSourceInstance = CreateTempSource();
        if (tempSourceInstance == null) return;

        try
        {
            // 构建并缓存损坏模型路径字典（用于后续判断缺失）
            cachedDamagedPathDict = BuildFullPathDict(damagedModel.transform);
            Dictionary<string, Transform> sourcePathDict = BuildFullPathDict(tempSourceInstance.transform);
            Debug.Log($"路径字典构建完成：损坏模型 {cachedDamagedPathDict.Count} 个节点，源模型 {sourcePathDict.Count} 个节点");

            // ========== 缺失骨骼检测 ==========
            Transform boneCompareRoot;
            if (customRootBone != null)
            {
                string customRootFullPath = GetFullPathRelativeToRoot(customRootBone, damagedModel.transform);
                boneCompareRoot = FindTransformByFullPath(tempSourceInstance.transform, customRootFullPath);
                if (boneCompareRoot == null)
                {
                    SetMessage($"在源模型中未找到指定的根节点 '{customRootBone.name}'，请检查路径是否匹配", MessageType.Error);
                    return;
                }
                Debug.Log($"使用手动指定的骨骼根节点：{customRootBone.name}");
            }
            else
            {
                boneCompareRoot = FindSkeletonRoot(tempSourceInstance.transform);
            }

            string boneRootFullPath = GetFullPathRelativeToRoot(boneCompareRoot, tempSourceInstance.transform);
            if (!cachedDamagedPathDict.ContainsKey(boneRootFullPath))
            {
                SetMessage($"损坏模型中缺少骨骼根节点 '{boneCompareRoot.name}'，请先确保根节点存在", MessageType.Error);
                Debug.LogError($"损坏模型中缺少骨骼根节点，路径：{boneRootFullPath}");
                return;
            }

            foreach (Transform child in boneCompareRoot)
            {
                string childFullPath = boneRootFullPath + "/" + child.name;
                missingBonesTree.AddRange(FindMissingBones(child, cachedDamagedPathDict, childFullPath));
            }

            // ========== 网格物体检测 ==========
            Transform meshCompareRoot = tempSourceInstance.transform;
            CollectAllMeshes(meshCompareRoot, cachedDamagedPathDict, "", allMeshObjectsTree);

            // 清空并重建“缺失/无效”列表
            missingMeshObjectsTree.Clear();
            foreach (var meshNode in allMeshObjectsTree)
            {
                // 1) 路径完全缺失 -> 走“缺失网格”逻辑（红色）
                bool pathMissing = !cachedDamagedPathDict.ContainsKey(meshNode.fullPath);
                if (pathMissing)
                {
                    meshNode.isInvalidSkinnedMesh = false;
                    missingMeshObjectsTree.Add(meshNode);
                    continue;
                }

                // 2) 路径存在 -> 判定该路径上的SMR是否“无效”（sharedMesh==null / 无SMR / 骨骼全空）
                if (IsInvalidSkinnedMeshOnDamaged(damagedModel, meshNode.fullPath))
                {
                    meshNode.isInvalidSkinnedMesh = true; // 黄色
                    missingMeshObjectsTree.Add(meshNode);
                }
            }

            // ========== 结果处理 ==========
            int totalMissingBones = GetTotalNodeCount(missingBonesTree);
            int totalMissingMeshes = GetTotalNodeCount(missingMeshObjectsTree);
            int totalAllMeshes = GetTotalNodeCount(allMeshObjectsTree);

            if (totalMissingBones == 0 && totalMissingMeshes == 0)
            {
                SetMessage(showAllMeshObjects
                    ? $"未检测到缺失骨骼！共识别到 {totalAllMeshes} 个网格物体"
                    : "未检测到缺失/无效网格物体！", MessageType.Info);
            }
            else
            {
                string meshInfo = showAllMeshObjects
                    ? $"共识别到 {totalAllMeshes} 个网格物体（其中缺失/无效 {totalMissingMeshes} 个）"
                    : $"检测到 {totalMissingMeshes} 个缺失/无效网格物体";
                SetMessage($"检测到 {totalMissingBones} 个缺失骨骼，{meshInfo}", MessageType.Warning);
            }

            Debug.Log($"===== 检查完成：{totalMissingBones} 个缺失骨骼，{totalMissingMeshes} 个缺失/无效网格（共{totalAllMeshes}个网格） =====");
        }
        catch (System.Exception e)
        {
            SetMessage($"检查时发生异常：{e.Message}", MessageType.Error);
            Debug.LogError($"检查异常：{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            CleanupTempSource(tempSourceInstance);
        }
    }

    // ---------- 绘制骨骼树UI ----------
    private void DrawBoneTree(List<BoneNode> nodes, int indentLevel)
    {
        foreach (var node in nodes)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(indentLevel * 20);

            bool hasChildren = node.children.Count > 0;
            if (hasChildren)
            {
                node.isExpanded = EditorGUILayout.Foldout(node.isExpanded, node.name, true);
            }
            else
            {
                EditorGUILayout.LabelField(" " + node.name);
            }

            if (GUILayout.Button("重建此骨骼及其子级", GUILayout.Width(150)))
            {
                RecreateBoneChain(node);
            }
            EditorGUILayout.EndHorizontal();

            if (hasChildren && node.isExpanded)
            {
                DrawBoneTree(node.children, indentLevel + 1);
            }
        }
    }

    // ---------- 绘制网格树UI（红色=缺失；黄色=无效；加粗=刚修复） ----------
    private void DrawMeshTree(List<BoneNode> nodes, int indentLevel)
    {
        foreach (var node in nodes)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(indentLevel * 20);

            // 1. 路径缺失（红色），2. 路径存在但无效SMR（黄色）
            bool isMissing = cachedDamagedPathDict != null && !cachedDamagedPathDict.ContainsKey(node.fullPath);
            bool isInvalid = !isMissing && node.isInvalidSkinnedMesh;
            bool isRecentlyFixed = recentlyFixedMeshPaths.Contains(node.fullPath);

            // 保存原始GUI状态
            Color originalColor = GUI.color;
            GUIStyle originalStyle = EditorStyles.label;

            // 设置颜色
            if (isMissing)
                GUI.color = Color.red;
            else if (isInvalid)
                GUI.color = Color.yellow;

            // 加粗样式（刚修复）
            GUIStyle displayStyle = originalStyle;
            if (isRecentlyFixed)
            {
                displayStyle = new GUIStyle(originalStyle) { fontStyle = FontStyle.Bold };
            }

            // 绘制
            bool hasChildren = node.children.Count > 0;
            if (hasChildren)
            {
                node.isExpanded = EditorGUILayout.Foldout(node.isExpanded, node.name, true, displayStyle);
            }
            else
            {
                EditorGUILayout.LabelField(" " + node.name, displayStyle);
            }

            if (GUILayout.Button("修复此网格物体", GUILayout.Width(150)))
            {
                FixMeshObject(node);
            }

            GUI.color = originalColor;
            EditorGUILayout.EndHorizontal();

            if (hasChildren && node.isExpanded)
            {
                DrawMeshTree(node.children, indentLevel + 1);
            }
        }
    }

    // ---------- 重建骨骼链 ----------
    private void RecreateBoneChain(BoneNode targetNode)
    {
        Undo.RegisterFullObjectHierarchyUndo(damagedModel, "重建骨骼链");
        ClearMessage();

        GameObject tempSourceInstance = CreateTempSource();
        if (tempSourceInstance == null) return;

        try
        {
            Transform sourceBone = FindTransformByFullPath(tempSourceInstance.transform, targetNode.fullPath);
            if (sourceBone == null)
            {
                SetMessage($"源模型中找不到骨骼 {targetNode.name}，重建失败", MessageType.Error);
                return;
            }

            RecreateBoneRecursive(sourceBone, tempSourceInstance.transform);
            EditorUtility.SetDirty(damagedModel);

            CheckBonesAndMeshes();

            SetMessage($"骨骼 {targetNode.name} 及其子级重建完成！", MessageType.Info);
            Debug.Log($"骨骼 {targetNode.fullPath} 及其子级重建完成");
        }
        catch (System.Exception e)
        {
            SetMessage($"重建骨骼时发生异常：{e.Message}", MessageType.Error);
            Debug.LogError($"重建骨骼异常：{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            CleanupTempSource(tempSourceInstance);
            GUIUtility.ExitGUI();
        }
    }

    // ---------- 实际蒙皮网格修复逻辑（记录刚修复的网格） ----------
    private void FixMeshObject(BoneNode targetNode)
    {
        ClearMessage();
        recentlyFixedMeshPaths.Add(targetNode.fullPath);

        GameObject tempSourceInstance = CreateTempSource();
        if (tempSourceInstance == null) return;

        try
        {
            Transform sourceMeshTransform = FindTransformByFullPath(tempSourceInstance.transform, targetNode.fullPath);
            if (sourceMeshTransform == null)
            {
                SetMessage($"源模型中找不到网格物体 {targetNode.name}", MessageType.Error);
                return;
            }

            Transform targetMeshTransform = FindTransformByFullPath(damagedModel.transform, targetNode.fullPath);
            if (targetMeshTransform == null)
            {
                targetMeshTransform = CreateMissingMeshNode(targetNode);
                if (targetMeshTransform == null)
                {
                    SetMessage($"无法创建网格物体节点 {targetNode.name}", MessageType.Error);
                    return;
                }
            }

            SkinnedMeshRenderer sourceMr = sourceMeshTransform.GetComponent<SkinnedMeshRenderer>();
            SkinnedMeshRenderer targetMr = targetMeshTransform.GetComponent<SkinnedMeshRenderer>();
            if (targetMr == null)
            {
                targetMr = targetMeshTransform.gameObject.AddComponent<SkinnedMeshRenderer>();
            }

            if (sourceMr == null || sourceMr.sharedMesh == null)
            {
                SetMessage($"源模型中的 {targetNode.name} 无有效蒙皮网格数据", MessageType.Error);
                return;
            }

            Undo.RecordObject(targetMr, "LGC蒙皮网格修复");

            BuildBoneMapCache(damagedModel.transform.root);
            SyncMeshMaterialsAndBounds(targetMr, sourceMr);

            Transform[] newBones = RebuildBonesArray(sourceMr.bones);
            List<string> lostBones = GetLostBones(newBones, sourceMr.bones);

            targetMr.bones = newBones;

            SetRootBone(targetMr, sourceMr, damagedModel.transform.root);
            RefreshRenderer(targetMr);

            ShowFixResult(lostBones, targetNode.name, targetMr);

            CheckBonesAndMeshes();
        }
        catch (System.Exception e)
        {
            SetMessage($"修复网格物体时发生异常：{e.Message}", MessageType.Error);
            Debug.LogError($"[LGC蒙皮工具] 修复 {targetNode.name} 失败：{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            CleanupTempSource(tempSourceInstance);
            GUIUtility.ExitGUI();
        }
    }

    // ---------- 创建缺失的网格节点 ----------
    private Transform CreateMissingMeshNode(BoneNode node)
    {
        string parentPath = GetParentPath(node.fullPath);
        Transform parentTransform = FindTransformByFullPath(damagedModel.transform, parentPath);
        if (parentTransform == null)
        {
            SetMessage($"无法找到父节点 {parentPath}，无法创建网格物体", MessageType.Error);
            return null;
        }

        GameObject newMeshObj = new GameObject(node.name);
        Undo.RegisterCreatedObjectUndo(newMeshObj, "创建缺失网格物体");
        newMeshObj.transform.SetParent(parentTransform);

        if (node.sourceTransform != null)
        {
            newMeshObj.transform.localPosition = node.sourceTransform.localPosition;
            newMeshObj.transform.localRotation = node.sourceTransform.localRotation;
            newMeshObj.transform.localScale = node.sourceTransform.localScale;
        }

        return newMeshObj.transform;
    }

    // ---------- 蒙皮修复核心方法 ----------
    private void BuildBoneMapCache(Transform root)
    {
        _boneMapCache = new Dictionary<string, Transform>();
        BuildBoneMapRecursive(root, _boneMapCache);
    }

    private void BuildBoneMapRecursive(Transform node, Dictionary<string, Transform> boneMap)
    {
        if (node == null) return;
        if (!boneMap.ContainsKey(node.name))
            boneMap[node.name] = node;
        for (int i = 0; i < node.childCount; i++)
            BuildBoneMapRecursive(node.GetChild(i), boneMap);
    }

    private void SyncMeshMaterialsAndBounds(SkinnedMeshRenderer target, SkinnedMeshRenderer source)
    {
        target.sharedMesh = source.sharedMesh;

        Material[] sourceMats = source.sharedMaterials;
        Material[] newMats = new Material[sourceMats.Length];
        for (int i = 0; i < sourceMats.Length; i++)
            newMats[i] = sourceMats[i] ?? CreateDefaultMaterial();

        target.sharedMaterials = newMats;
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

        if (source.rootBone != null)
        {
            string rootName = source.rootBone.name;
            if (!string.IsNullOrEmpty(rootName))
                _boneMapCache.TryGetValue(rootName, out newRootBone);
        }

        if (newRootBone == null)
        {
            string[] commonRootNames = { "Hips", "Bip001", "Bip01", "Root", "Pelvis", "Armature" };
            foreach (string name in commonRootNames)
            {
                if (_boneMapCache.TryGetValue(name, out newRootBone))
                    break;
            }
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
            renderer.enabled = false;
            renderer.enabled = true;

            SceneView.RepaintAll();
            EditorUtility.SetDirty(renderer);

            Selection.activeObject = null;
            EditorApplication.delayCall += () =>
            {
                Selection.activeObject = renderer.gameObject;
            };
        };
    }

    private void ShowFixResult(List<string> lostBones, string meshName, SkinnedMeshRenderer renderer)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"【{meshName}】修复完成：");

        float matchRate = _totalBones > 0 ? (float)_matchedBones / _totalBones : 0f;
        sb.AppendLine($"骨骼匹配: {_matchedBones}/{_totalBones} ({matchRate:P0})");
        sb.AppendLine($"根骨骼: {(renderer.rootBone != null ? renderer.rootBone.name : "未设置")}");

        if (lostBones.Count > 0)
        {
            sb.AppendLine($"缺失骨骼: {lostBones.Count}个");
            if (lostBones.Count <= 5)
                sb.Append(string.Join(", ", lostBones));
            else
                sb.Append($"{string.Join(", ", lostBones.GetRange(0, 5))}...等{lostBones.Count}个");

            SetMessage(sb.ToString(), MessageType.Warning);
        }
        else
        {
            sb.Append("✓ 所有骨骼已匹配");
            SetMessage(sb.ToString(), MessageType.Info);
        }
    }

    private Material CreateDefaultMaterial()
    {
        return new Material(Shader.Find("Standard"));
    }

    // ---------- 骨骼重建逻辑 ----------
    private void RecreateBoneRecursive(Transform sourceBone, Transform sourceModelRoot)
    {
        string boneFullPath = GetFullPathRelativeToRoot(sourceBone, sourceModelRoot);
        Transform existingBone = FindTransformByFullPath(damagedModel.transform, boneFullPath);
        if (existingBone != null)
        {
            return;
        }

        string parentFullPath = GetParentPath(boneFullPath);
        Transform parentTransform = FindTransformByFullPath(damagedModel.transform, parentFullPath);
        if (parentTransform == null)
        {
            Transform sourceParent = sourceBone.parent;
            if (sourceParent != null)
            {
                RecreateBoneRecursive(sourceParent, sourceModelRoot);
            }

            parentTransform = FindTransformByFullPath(damagedModel.transform, parentFullPath);
            if (parentTransform == null)
            {
                Debug.LogError($"无法重建骨骼 {sourceBone.name}，父级节点无法找到/创建");
                return;
            }
        }

        GameObject newBone = new GameObject(sourceBone.name);
        Undo.RegisterCreatedObjectUndo(newBone, "创建骨骼");
        newBone.transform.SetParent(parentTransform);
        newBone.transform.localPosition = sourceBone.localPosition;
        newBone.transform.localRotation = sourceBone.localRotation;
        newBone.transform.localScale = sourceBone.localScale;
        Debug.Log($"成功创建骨骼：{boneFullPath}");

        foreach (Transform child in sourceBone)
        {
            if (child.GetComponent<Renderer>() == null && child.GetComponent<SkinnedMeshRenderer>() == null)
            {
                RecreateBoneRecursive(child, sourceModelRoot);
            }
        }
    }

    // ---------- “无效蒙皮网格”判定 ----------
    private bool IsInvalidSkinnedMeshOnDamaged(GameObject damagedRoot, string fullPath)
    {
        var tr = FindTransformByFullPath(damagedRoot.transform, fullPath);
        if (tr == null) return false; // 路径都不存在，交给“缺失”逻辑处理

        var smr = tr.GetComponent<SkinnedMeshRenderer>();
        if (smr == null) return true; // 有节点但没有SMR

        if (smr.sharedMesh == null) return true;

        var bones = smr.bones;
        if (bones == null || bones.Length == 0) return true;

        bool allNull = true;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null) { allNull = false; break; }
        }
        if (allNull) return true;

        return false;
    }

    // ---------- 辅助工具方法 ----------
    private string GetFullPathRelativeToRoot(Transform target, Transform root)
    {
        if (target == root) return "";

        List<string> pathSegments = new List<string>();
        Transform current = target;

        while (current != null && current != root)
        {
            pathSegments.Add(current.name);
            current = current.parent;
        }

        if (current == null)
        {
            Debug.LogWarning($"节点 {target.name} 不在根节点 {root.name} 的层级下");
            return target.name;
        }

        pathSegments.Reverse();
        return string.Join("/", pathSegments);
    }

    private Transform FindTransformByFullPath(Transform root, string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return root;

        string[] pathSegments = fullPath.Split('/');
        Transform current = root;

        foreach (string segment in pathSegments)
        {
            if (current == null) return null;
            current = current.Find(segment);
        }
        return current;
    }

    private string GetParentPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "";
        int lastSlashIndex = fullPath.LastIndexOf('/');

        return lastSlashIndex == -1 ? "" : fullPath.Substring(0, lastSlashIndex);
    }

    private int GetTotalNodeCount(List<BoneNode> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count++;
            count += GetTotalNodeCount(node.children);
        }
        return count;
    }

    // ---------- 节点内部类 ----------
    [System.Serializable]
    private class BoneNode
    {
        public string name;
        public string fullPath;
        public Transform sourceTransform;
        public List<BoneNode> children = new List<BoneNode>();
        public bool isExpanded = false;

        // 是否“无效蒙皮网格”（路径存在但SMR异常/mesh为空/骨骼空）
        public bool isInvalidSkinnedMesh = false;
    }

    // ====================== 智能单网格自动分流支持 ======================

    /// <summary>
    /// 判断槽位①对象是否“像”单个网格：必须是带 SkinnedMeshRenderer 的子物体。
    /// </summary>
    private bool LooksLikeSingleMeshObject(GameObject go)
    {
        if (go == null) return false;
        var smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr == null) return false;
        if (go.transform.parent == null) return false; // 作为场景根更像整人物
        return true;
    }

    /// <summary>
    /// 在 FBX 临时实例里按名称“最佳匹配”网格：先完全匹配，其次忽略大小写，再次包含匹配。
    /// 若 _sourceMeshPathHint 提供了线索，可先尝试路径直达。
    /// </summary>
    private Transform FindBestMatchMesh(Transform root, string targetName)
    {
        if (root == null) return null;

        // 若 UI 从子对象提升时留下了路径/名称提示，先尝试路径直达
        if (!string.IsNullOrEmpty(_sourceMeshPathHint))
        {
            var hinted = FindTransformByFullPath(root, _sourceMeshPathHint);
            if (hinted != null && hinted.GetComponent<SkinnedMeshRenderer>() != null)
                return hinted;
        }

        if (string.IsNullOrEmpty(targetName)) return null;

        var all = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        var exact = all.FirstOrDefault(m => m.name == targetName);
        if (exact != null) return exact.transform;

        var icase = all.FirstOrDefault(m => string.Equals(m.name, targetName, System.StringComparison.OrdinalIgnoreCase));
        if (icase != null) return icase.transform;

        var contains = all.FirstOrDefault(m => m.name.IndexOf(targetName, System.StringComparison.OrdinalIgnoreCase) >= 0);
        return contains != null ? contains.transform : null;
    }

    /// <summary>
    /// 单网格修复入口：当槽位①为单个 SMR 物体时，被“检查骨骼与网格”按钮自动调用。
    /// 不做整人物巡检，不展开网格树，且通过双通道临时加载避免污染场景。
    /// </summary>
    private void FixSingleMeshFromDamagedSlot()
    {
        ClearMessage();

        if (damagedModel == null || sourceFBX == null)
        {
            SetMessage("请同时指定：单个网格对象（场景）与原始 FBX 资产（Project）。", MessageType.Warning);
            return;
        }

        var targetMr = damagedModel.GetComponent<SkinnedMeshRenderer>();
        if (targetMr == null)
        {
            SetMessage("拖入的对象不是单个蒙皮网格，无法走单网格修复。", MessageType.Warning);
            return;
        }

        GameObject temp = CreateTempSource();
        if (temp == null) return;

        try
        {
            // 定位源网格（路径提示优先，其次名称匹配）
            Transform srcMeshTr = FindBestMatchMesh(temp.transform, targetMr.name);
            if (srcMeshTr == null)
            {
                SetMessage($"在原始 FBX 中找不到与 {targetMr.name} 可匹配的蒙皮网格。", MessageType.Error);
                return;
            }

            var sourceMr = srcMeshTr.GetComponent<SkinnedMeshRenderer>();
            if (sourceMr == null || sourceMr.sharedMesh == null)
            {
                SetMessage("匹配到的源网格不包含有效的蒙皮数据（sharedMesh 为空）。", MessageType.Error);
                return;
            }

            BuildBoneMapCache(targetMr.transform.root);

            Undo.RecordObject(targetMr, "LGC单网格骨骼修复");
            SyncMeshMaterialsAndBounds(targetMr, sourceMr);

            Transform[] newBones = RebuildBonesArray(sourceMr.bones);
            List<string> lostBones = GetLostBones(newBones, sourceMr.bones);
            targetMr.bones = newBones;

            SetRootBone(targetMr, sourceMr, targetMr.transform.root);
            RefreshRenderer(targetMr);

            ShowFixResult(lostBones, targetMr.name, targetMr);

            string fullPath = GetFullPathRelativeToRoot(targetMr.transform, targetMr.transform.root);
            if (!string.IsNullOrEmpty(fullPath))
                recentlyFixedMeshPaths.Add(fullPath);
        }
        catch (System.Exception e)
        {
            SetMessage($"单网格修复失败：{e.Message}", MessageType.Error);
            Debug.LogException(e);
        }
        finally
        {
            CleanupTempSource(temp);
            GUIUtility.ExitGUI();
        }
    }
}
#endif
