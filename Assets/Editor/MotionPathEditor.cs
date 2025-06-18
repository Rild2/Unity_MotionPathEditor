using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System;

// вспомогательный класс
[Serializable]
public class BezierPoint
{
    public Vector3 position;
    public Vector3 inTangentPoint;   // Управляющая точка для входящей кривой
    public Vector3 outTangentPoint;  // Управляющая точка для исходящей кривой

    public BezierPoint(Vector3 pos)
    {
        position = pos;
        // По умолчанию управляющие точки находятся там же, где и основная
        inTangentPoint = pos;
        outTangentPoint = pos;
    }
}

public class MotionPathEditor : EditorWindow
{
    // По сути, основа
    private List<BezierPoint> KeyFramePositions;
    private string[] bindingProperties = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z" };

    // Вспомогательные переменные для меню
    private GameObject Obj;
    private int anim_index;
    private bool CurveDrawn;
    private AnimationClip CurrentAnimation;
    private AnimationClip _oldAnimation;
    private GUIStyle numerationTextStyle;

    // Галочки
    private bool handlesSnapping = false;
    private bool alternativeHandles = false;
    private bool showBezierHandles = true;
    private bool enableNumeration = true;

    // Окно настроек
    private bool settingsFoldout = false;

    private float settingsHandlesSnapDistance;

    private float settingsHandleSize;
    private int settingsNumerationTextSize;

    private Color settingsPathColor;
    private Color settingsHandleColor;
    private Color settingsBezierHandleColor;
    private Color settingsNumerationTextColor;
    private float settingsShiftTime;

    // Для сохранения настроек
    private const string FLOAT_KEY_HandleSize = "MPE_HandleSize";
    private const string INT_KEY_NumerationTextSize = "MPE_NumerationTextSize";

    private const string FLOAT_KEY_HandlesSnapDistance = "MPE_HandlesSnapDistance";

    private const string COLOR_KEY_PathColor = "MPE_PathColor";
    private const string COLOR_KEY_HandleColor = "MPE_HandleColor";
    private const string COLOR_KEY_BezierHandleColor = "MPE_BezierHandleColor";
    private const string COLOR_KEY_LabelColor = "MPE_LabelColor";
    private const string FLOAT_KEY_ShiftTime = "MPE_ShiftTime";

    [MenuItem("Tools/MotionPathEditor")]
    public static void ShowWindow()
    {
        GetWindow<MotionPathEditor>("[MPE] Menu");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        Undo.undoRedoPerformed += OnUndoRedo;

        settingsHandleSize = EditorPrefs.GetFloat(FLOAT_KEY_HandleSize, 0.1f);
        settingsNumerationTextSize = EditorPrefs.GetInt(INT_KEY_NumerationTextSize, 14);
        settingsHandlesSnapDistance = EditorPrefs.GetFloat(FLOAT_KEY_HandlesSnapDistance, EditorSnapSettings.move.x);
        settingsShiftTime = EditorPrefs.GetFloat(FLOAT_KEY_ShiftTime, 1f);

        // Базовый цвет - белый
        ColorUtility.TryParseHtmlString(EditorPrefs.GetString(COLOR_KEY_PathColor, "FFFFFFFF"), out settingsPathColor);

        ColorUtility.TryParseHtmlString(EditorPrefs.GetString(COLOR_KEY_HandleColor, "FFFFFFFF"), out settingsHandleColor);

        ColorUtility.TryParseHtmlString(EditorPrefs.GetString(COLOR_KEY_BezierHandleColor, "FFFFFFFF"), out settingsBezierHandleColor);

        ColorUtility.TryParseHtmlString(EditorPrefs.GetString(COLOR_KEY_LabelColor, "FFFFFFFF"), out settingsNumerationTextColor);

        UpdateNumerationTextStyle();
    }

    private void OnDisable()
    {
        // Сохраняем настройки
        EditorPrefs.SetFloat(FLOAT_KEY_HandleSize, settingsHandleSize);
        EditorPrefs.SetInt(INT_KEY_NumerationTextSize, settingsNumerationTextSize);
        EditorPrefs.SetFloat(FLOAT_KEY_ShiftTime, settingsShiftTime);
        EditorPrefs.SetFloat(FLOAT_KEY_HandlesSnapDistance, settingsHandlesSnapDistance);

        EditorPrefs.SetString(COLOR_KEY_PathColor, "#" + ColorUtility.ToHtmlStringRGBA(settingsPathColor));
        EditorPrefs.SetString(COLOR_KEY_HandleColor, "#" + ColorUtility.ToHtmlStringRGBA(settingsHandleColor));
        EditorPrefs.SetString(COLOR_KEY_BezierHandleColor, "#" + ColorUtility.ToHtmlStringRGBA(settingsBezierHandleColor));
        EditorPrefs.SetString(COLOR_KEY_LabelColor, "#" + ColorUtility.ToHtmlStringRGBA(settingsNumerationTextColor));

        SceneView.duringSceneGui -= OnSceneGUI;
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    // Вызывается при каждом Ctrl+Z или Ctrl+Y - меняем список (это тут же отображается в OnSceneGUI). 
    // По сути надстройка над AddKeyframe и DeleteKeyframe - в которые уже встроено Undo, где меняется текущая анимация.
    private void OnUndoRedo()
    {
        if (CurrentAnimation != null && CurveDrawn)
            KeyFramePositions = GetBesiersOnKeyframes(CurrentAnimation);
    }

    // Отдельная (неуместно большая) функция для нумерации позици2й
    private void UpdateNumerationTextStyle()
    {
        if (numerationTextStyle == null)
        {
            numerationTextStyle = new GUIStyle();
            numerationTextStyle.alignment = TextAnchor.LowerRight;
        }

        numerationTextStyle.normal.textColor = settingsNumerationTextColor;
        numerationTextStyle.fontSize = settingsNumerationTextSize;
    }

    private Vector2 _scrollpos;
    public void OnGUI()
    {
        Obj = EditorGUILayout.ObjectField("Animated object", Obj, typeof(GameObject), true) as GameObject;

        if (Obj == null)
        {
            EditorGUILayout.HelpBox("Select a GameObject with an Animator component.", MessageType.Info);
            return;
        }

        Animator animator = Obj.GetComponent<Animator>();

        if (animator == null || animator.runtimeAnimatorController == null)
        {
            EditorGUILayout.HelpBox("Selected GameObject must have an Animator.", MessageType.Warning);
            return;
        }

        if (animator.runtimeAnimatorController.animationClips.Length == 0)
        {
            EditorGUILayout.HelpBox("Selected GameObject must have at least one animation.", MessageType.Warning);
            return;
        }

        // начинаем "секцию" с прокруткой окна только после всех return.
        _scrollpos = EditorGUILayout.BeginScrollView(_scrollpos, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

        var clips = animator.runtimeAnimatorController.animationClips;

        string[] animation_names = clips.Select(x => x.name).ToArray();

        anim_index = EditorGUILayout.Popup("Choose animation clip:", anim_index, animation_names);

        CurrentAnimation = clips[anim_index];

        // Для избранных, которые создадут объект и аниматор с одной пустой анимацией.
        if (CurrentAnimation.empty)
        {
            CreateDefaultCurve(CurrentAnimation);
            return;
        }

        // Если меняем анимацию, то обновляем кривую.
        if (_oldAnimation != CurrentAnimation)
        {
            _oldAnimation = CurrentAnimation;
            KeyFramePositions = GetBesiersOnKeyframes(CurrentAnimation);
        }

        EditorGUI.BeginChangeCheck();

        if (!CurveDrawn)
        {
            if (GUILayout.Button("Draw curve"))
            {
                KeyFramePositions = GetBesiersOnKeyframes(CurrentAnimation);
                CurveDrawn = true;
            }
        }
        else
        {
            if (GUILayout.Button("Erase curve"))
            {
                KeyFramePositions.Clear();
                CurveDrawn = false;
            }
        }

        handlesSnapping = EditorGUILayout.Toggle("Snapping Handles", handlesSnapping);

        EditorGUILayout.Separator();

        alternativeHandles = EditorGUILayout.Toggle("Alternative Handles", alternativeHandles);
        showBezierHandles = EditorGUILayout.Toggle("Show Bezier Handles", showBezierHandles);
        enableNumeration = EditorGUILayout.Toggle("Enable frames numeration", enableNumeration);

        settingsFoldout = EditorGUILayout.Foldout(settingsFoldout, "Additional settings", true, EditorStyles.foldoutHeader);

        if (settingsFoldout)
        {
            EditorGUI.indentLevel++;

            if (handlesSnapping)
                settingsHandlesSnapDistance = EditorGUILayout.FloatField("Handles snap distance", settingsHandlesSnapDistance);

            // На нуле оставлять нельзя, а то будут ошибки
            if (settingsHandlesSnapDistance < 0.01f)
                settingsHandlesSnapDistance = 0.01f;

            settingsShiftTime = EditorGUILayout.FloatField("Keyframe time shift", settingsShiftTime);

            // А тут ошибок будет кратно больше
            if (settingsShiftTime < 0.01f)
                settingsShiftTime = 0.01f;

            EditorGUILayout.Separator();

            settingsHandleSize = EditorGUILayout.Slider("Handles size", settingsHandleSize, 0.05f, 0.5f);
            settingsPathColor = EditorGUILayout.ColorField("Path color", settingsPathColor);
            settingsHandleColor = EditorGUILayout.ColorField("Handles color", settingsHandleColor);

            if (showBezierHandles)
                settingsBezierHandleColor = EditorGUILayout.ColorField("Bezier handles color", settingsBezierHandleColor);

            if (enableNumeration)
            {
                EditorGUILayout.Separator();
                settingsNumerationTextSize = (int)EditorGUILayout.Slider("Numeration text size", settingsNumerationTextSize, 8, 80);
                settingsNumerationTextColor = EditorGUILayout.ColorField("Numeration text color", settingsNumerationTextColor);
            }

            EditorGUI.indentLevel--;
        }

        if (EditorGUI.EndChangeCheck())
        {
            SceneView.RepaintAll();
            UpdateNumerationTextStyle();
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("In the Scene View:\n- Drag handles to move keyframes.\n- Ctrl+Click and drag on a handle to insert a new keyframe before it.\n- Shift+Click on a handle to delete it.\n- Enable alternative handles for more precise positioning\n- Hold Alt or toggle off Bezier handles.\n- Check animation tab if there are any path errors.", MessageType.Info);

        EditorGUILayout.EndScrollView();
    }

    // Функция для приклеивания хэндла к сетке (продублировано ещё дважды)
    private Vector3 SnapHandle(Vector3 position, float snapDistance)
    {
        position.x = Mathf.Round(position.x / snapDistance) * snapDistance;
        position.y = Mathf.Round(position.y / snapDistance) * snapDistance;
        position.z = Mathf.Round(position.z / snapDistance) * snapDistance;
        return position;
    }

    // Тут осталась неприятная ошибка - при создании нового хэндла от любого кроме первого, выделяется для редактирования позиции именно старый, а не новый. Но в борьбе с ней полегло много хороших часов...
    public void OnSceneGUI(SceneView sceneView)
    {
        if (CurveDrawn == false || CurrentAnimation == null)
            return;

        Undo.RecordObject(CurrentAnimation, "[MPE] Modify Animation Path");

        bool deleted = false;
        bool inserted = false;
        int InsertedHandleIndex = 0;
        Vector3 InsertedHandlePosition = Vector3.zero;

        EditorGUI.BeginChangeCheck();

        // Действия в цикле вызываются каждый раз, когда меняется положение одной из точек (да и вообще каждый кадр).
        for (int i = 0; i < KeyFramePositions.Count; i++)
        {
            BezierPoint point = KeyFramePositions[i];

            float handleSize = HandleUtility.GetHandleSize(point.position) * settingsHandleSize;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            EventType HandleEvent = Event.current.GetTypeForControl(controlID);

            // логика удаления/добавления
            if (HandleEvent == EventType.MouseDown && Event.current.control && HandleUtility.nearestControl == controlID)
            {
                InsertedHandleIndex = i;
                inserted = true;
                InsertedHandlePosition = point.position;
            }

            if (HandleEvent == EventType.MouseDown && Event.current.shift && HandleUtility.nearestControl == controlID)
            {
                InsertedHandleIndex = i;
                deleted = true;
                break;
            }

            Handles.color = settingsHandleColor;

            // Создаём хэндлы для точек позиции
            Vector3 handle_pos = Vector3.zero;

            if (alternativeHandles)
                handle_pos = Handles.PositionHandle(point.position, Quaternion.identity);
            else
                handle_pos = Handles.FreeMoveHandle(controlID, point.position, handleSize, Vector3.zero, Handles.CubeHandleCap);

            if (enableNumeration)
                Handles.Label(handle_pos + Vector3.up * 0.2f, i.ToString(), numerationTextStyle);

            if (showBezierHandles == true && !Event.current.alt && !Event.current.control && !Event.current.shift)
            {
                Handles.color = settingsBezierHandleColor;

                // Точка для настройки входной кривой (отсутствует у первого основного хэндла)
                if (i != 0)
                {
                    Handles.DrawLine(handle_pos, point.inTangentPoint);
                    Vector3 inTangentPointPos = Handles.FreeMoveHandle(point.inTangentPoint, handleSize * 0.8f, Vector3.zero, Handles.SphereHandleCap);

                    if (handlesSnapping)
                        inTangentPointPos = SnapHandle(inTangentPointPos, settingsHandlesSnapDistance);

                    point.inTangentPoint = inTangentPointPos;

                    // Эта часть для "зеркальности" точек.
                    // Позиция второй точки считается так: в скобках вычисляется вектор который ведёт от первой точки к основной, Затем он прибавляется к позиции основной точки.
                    point.outTangentPoint = point.position + (point.position - point.inTangentPoint);
                }
                // Точка для настройки выходной кривой (отсутствует у последнего хэндла)
                if (i < KeyFramePositions.Count - 1)
                {
                    Handles.DrawLine(handle_pos, point.outTangentPoint);
                    Vector3 outTangentPointPos = Handles.FreeMoveHandle(point.outTangentPoint, handleSize * 0.8f, Vector3.zero, Handles.SphereHandleCap);

                    if (handlesSnapping)
                        outTangentPointPos = SnapHandle(outTangentPointPos, settingsHandlesSnapDistance);

                    point.outTangentPoint = outTangentPointPos;
                    point.inTangentPoint = point.position + (point.position - point.outTangentPoint);
                }
            }

            if (handlesSnapping)
                handle_pos = SnapHandle(handle_pos, settingsHandlesSnapDistance);

            // Двигаем точки для настройки кривых вместе с основным хэндлом
            // И делаем это только после того, как устаканилась его позиция
            point.inTangentPoint += handle_pos - point.position;
            point.outTangentPoint += handle_pos - point.position;

            KeyFramePositions[i].position = handle_pos;
        }

        if (inserted)
        {
            KeyFramePositions.Insert(InsertedHandleIndex, new BezierPoint(InsertedHandlePosition));
            AddKeyframe(CurrentAnimation, InsertedHandleIndex, InsertedHandlePosition, settingsShiftTime);
        }

        if (deleted)
        {
            KeyFramePositions.RemoveAt(InsertedHandleIndex);
            DeleteKeyframe(CurrentAnimation, InsertedHandleIndex, settingsShiftTime);
        }

        if (EditorGUI.EndChangeCheck())
        {
            SetBeziersOnKeyframes(CurrentAnimation, KeyFramePositions);
        }

        Handles.color = settingsPathColor;

        for (int i = 0; i < KeyFramePositions.Count - 1; i++)
        {
            BezierPoint startPoint = KeyFramePositions[i];
            BezierPoint endPoint = KeyFramePositions[i + 1];

            Vector3 StartTangent = startPoint.outTangentPoint;
            Vector3 EndTangent = endPoint.inTangentPoint;

            Handles.DrawBezier(startPoint.position, endPoint.position, StartTangent, EndTangent, Color.white, null, 5f);
        }
    }

    public List<BezierPoint> GetBesiersOnKeyframes(AnimationClip clip)
    {
        // Определяем привязки для каждой оси
        EditorCurveBinding xBind = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x");
        EditorCurveBinding yBind = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.y");
        EditorCurveBinding zBind = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.z");

        // Получаем кривые из клипа
        AnimationCurve xCurve = AnimationUtility.GetEditorCurve(CurrentAnimation, xBind);
        AnimationCurve yCurve = AnimationUtility.GetEditorCurve(CurrentAnimation, yBind);
        AnimationCurve zCurve = AnimationUtility.GetEditorCurve(CurrentAnimation, zBind);

        var bezierPoints = new List<BezierPoint>();

        for (int i = 0; i < xCurve.keys.Length; i++)
        {
            Vector3 pos = new Vector3(xCurve.keys[i].value, yCurve.keys[i].value, zCurve.keys[i].value);

            float time = xCurve.keys[i].time;

            float timeToPrev = 0;
            float timeToNext = 0;

            if (i > 0)
                timeToPrev = time - xCurve.keys[i - 1].time;

            if (i < xCurve.keys.Length - 1)
                timeToNext = xCurve.keys[i + 1].time - time;

            Vector3 inTangentVec = new Vector3(xCurve.keys[i].inTangent, yCurve.keys[i].inTangent, zCurve.keys[i].inTangent);
            Vector3 outTangentVec = new Vector3(xCurve.keys[i].outTangent, yCurve.keys[i].outTangent, zCurve.keys[i].outTangent);

            var point = new BezierPoint(pos);

            point.inTangentPoint = pos - (inTangentVec * timeToPrev / 3.0f);
            point.outTangentPoint = pos + (outTangentVec * timeToNext / 3.0f);

            bezierPoints.Add(point);
        }

        return bezierPoints;
    }

    public void SetBeziersOnKeyframes(AnimationClip clip, List<BezierPoint> bezierPoints)
    {
        var xBinding = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x");
        var yBinding = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.y");
        var zBinding = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.z");

        AnimationCurve xCurve = AnimationUtility.GetEditorCurve(CurrentAnimation, xBinding);
        AnimationCurve yCurve = AnimationUtility.GetEditorCurve(CurrentAnimation, yBinding);
        AnimationCurve zCurve = AnimationUtility.GetEditorCurve(CurrentAnimation, zBinding);

        // Копируем массивы ключевых кадров.
        Keyframe[] xKeys = xCurve.keys;
        Keyframe[] yKeys = yCurve.keys;
        Keyframe[] zKeys = zCurve.keys;

        for (int i = 0; i < bezierPoints.Count; i++)
        {
            var point = bezierPoints[i];

            // Определяем время до предыдущей и следующей точки
            float timeToPrev = 0;
            float timeToNext = 0;

            if (i > 0)
                timeToPrev = xKeys[i].time - xKeys[i - 1].time;

            if (i < xKeys.Length - 1)
                timeToNext = xKeys[i + 1].time - xKeys[i].time;

            // Тангенсы рассчитываются по формуле (разница позиций) * 3 / время
            // По разности позиций получаем "вектор-расстояние"
            // Мгновенная скорость в начальной точке кривой Безье (P₀) равна вектору, идущему от этой точки к ее управляющей точке (P₁), и она равна 3. (взяли производную от общей формулы, подставив в неё время равное 0)
            // Делим на время чтобы получить собственно, сам тангенс, отношение вектора-расстояние к времени.

            // Входящий тангенс
            Vector3 inTangentVec = Vector3.zero;
            if (timeToPrev > 0)
            {
                inTangentVec = (point.position - point.inTangentPoint) * 3.0f / timeToPrev;
            }

            // Исходящий тангенс
            Vector3 outTangentVec = Vector3.zero;
            if (timeToNext > 0)
            {
                outTangentVec = (point.outTangentPoint - point.position) * 3.0f / timeToNext;
            }

            xKeys[i].value = point.position.x;
            xKeys[i].inTangent = inTangentVec.x;
            xKeys[i].outTangent = outTangentVec.x;

            yKeys[i].value = point.position.y;
            yKeys[i].inTangent = inTangentVec.y;
            yKeys[i].outTangent = outTangentVec.y;

            zKeys[i].value = point.position.z;
            zKeys[i].inTangent = inTangentVec.z;
            zKeys[i].outTangent = outTangentVec.z;
        }

        // Присваиваем измененные массивы обратно кривым
        xCurve.keys = xKeys;
        yCurve.keys = yKeys;
        zCurve.keys = zKeys;

        // Применяем обновленные кривые к клипу
        AnimationUtility.SetEditorCurve(CurrentAnimation, xBinding, xCurve);
        AnimationUtility.SetEditorCurve(CurrentAnimation, yBinding, yCurve);
        AnimationUtility.SetEditorCurve(CurrentAnimation, zBinding, zCurve);
    }

    public void AddKeyframe(AnimationClip clip, int index, Vector3 pos, float shiftTime)
    {
        float pos_coord = 0;

        Undo.RecordObject(CurrentAnimation, "[MPE] Add Keyframe");

        for (int i = 0; i < bindingProperties.Length; i++)
        {
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve("", typeof(Transform), bindingProperties[i]);
            var curve = AnimationUtility.GetEditorCurve(clip, binding);

            if (index > curve.length)
                index = curve.length;

            var keyframes = curve.keys.ToList();

            if (binding.propertyName == bindingProperties[0])
                pos_coord = pos.x;
            if (binding.propertyName == bindingProperties[1])
                pos_coord = pos.y;
            if (binding.propertyName == bindingProperties[2])
                pos_coord = pos.z;

            // Сначала смещаем все ключевые кадры у каждой из кривых (по х, по у или по z) на заданное время
            for (int j = 0; j < keyframes.Count; j++)
            {
                if (j >= index)
                {
                    var keyframe = keyframes[j];
                    keyframe.time += shiftTime;
                    keyframes[j] = keyframe;
                }
            }

            // Затем вставляем наш новый кадр по времени одного из старых кадров (curve.keys() нигде не менялась), полученного по индексу
            // Также проверяем, может мы вставляем кадр последним, тогда смещаем его по времени относительно других
            if (index == curve.length)
                keyframes.Insert(index, new Keyframe(curve.keys[index - 1].time + shiftTime, pos_coord));
            else
                keyframes.Insert(index, new Keyframe(curve.keys[index].time, pos_coord));

            AnimationCurve newCurve = new AnimationCurve(keyframes.ToArray());
            AnimationUtility.SetEditorCurve(clip, binding, newCurve);
        }

        EditorUtility.SetDirty(clip);
    }

    public void DeleteKeyframe(AnimationClip clip, int index, float shiftTime)
    {
        Undo.RecordObject(CurrentAnimation, "[MPE] Delete Keyframe");

        for (int i = 0; i < bindingProperties.Length; i++)
        {
            EditorCurveBinding binding = EditorCurveBinding.FloatCurve("", typeof(Transform), bindingProperties[i]);
            var curve = AnimationUtility.GetEditorCurve(clip, binding);

            if (index < 0)
                index = 0;
            else if (index >= curve.length)
                index = curve.length - 1;

            var keyframes = curve.keys.ToList();

            keyframes.RemoveAt(index);

            for (int j = 0; j < keyframes.Count; j++)
            {
                if (j >= index)
                {
                    var keyframe = keyframes[j];
                    keyframe.time -= shiftTime;
                    keyframes[j] = keyframe;
                }
            }

            AnimationCurve newCurve = new AnimationCurve(keyframes.ToArray());
            AnimationUtility.SetEditorCurve(clip, binding, newCurve);
        }

        EditorUtility.SetDirty(clip);
    }

    private void CreateDefaultCurve(AnimationClip clip)
    {
        Undo.RecordObject(clip, "[MPE] Create Default Path");

        var startPos = new Vector3(-2, 0, 0);
        var endPos = new Vector3(2, 0, 0);

        var startX = new Keyframe(0.0f, startPos.x);
        var startY = new Keyframe(0.0f, startPos.y);
        var startZ = new Keyframe(0.0f, startPos.z);

        var endX = new Keyframe(1.0f, endPos.x);
        var endY = new Keyframe(1.0f, endPos.y);
        var endZ = new Keyframe(1.0f, endPos.z);

        var curveX = new AnimationCurve(startX, endX);
        var curveY = new AnimationCurve(startY, endY);
        var curveZ = new AnimationCurve(startZ, endZ);

        var bindingX = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x");
        var bindingY = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.y");
        var bindingZ = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.z");

        AnimationUtility.SetEditorCurve(clip, bindingX, curveX);
        AnimationUtility.SetEditorCurve(clip, bindingY, curveY);
        AnimationUtility.SetEditorCurve(clip, bindingZ, curveZ);

        EditorUtility.SetDirty(clip);
    }
}
