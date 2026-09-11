#pragma warning disable CS8600, CS8602, CS8603
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Interstellar.Voice;

public class VCTextField : MonoBehaviour
{
    private static VCTextField? _active;
    public static bool IsAnyActive => _active != null;

    private TMP_Text _text = null!;
    private TMP_Text _cursor = null!;
    private Image _bg = null!;
    private Image _cursorImg = null!;

    private string _input = "";
    private int _cursorPos;
    private int _selectBegin = -1;
    private float _cursorTimer;
    private string _hint = "";
    private int _charLimit = 40;

    public string Text => _input;
    public Action<string>? OnValueChanged;
    public Action? OnConfirm;

    private bool IsSelecting => _selectBegin != -1;
    private bool PressingShift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    public VCTextField(IntPtr ptr) : base(ptr) { }

    public void Init(RectTransform container, float fontSize, int charLimit, string hint, string initial)
    {
        _charLimit = charLimit;
        _hint = hint;

        _bg = gameObject.AddComponent<Image>();
        _bg.sprite = VCUiKit.PixelSprite;
        _bg.color = new Color(0.10f, 0.13f, 0.20f, 1f);

        var textObj = new GameObject("Text");
        textObj.transform.SetParent(container, false);
        var textRt = textObj.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.pivot = new Vector2(0.5f, 0.5f);
        textRt.offsetMin = new Vector2(14f, 6f);
        textRt.offsetMax = new Vector2(-14f, -6f);
        _text = textObj.AddComponent<TextMeshProUGUI>();
        try
        {
            var f = VCUiKit.Font;
            if (f != null) _text.font = f;
            var m = VCUiKit.FontMaterial;
            if (m != null) _text.fontSharedMaterial = m;
        }
        catch { }
        _text.fontSize = fontSize;
        _text.color = Color.white;
        _text.alignment = TextAlignmentOptions.Left;
        _text.raycastTarget = false;
        _text.text = string.IsNullOrEmpty(initial) ? _hint : initial;
        _text.alpha = string.IsNullOrEmpty(initial) ? 0.45f : 1f;

        var cursorObj = new GameObject("Cursor");
        cursorObj.transform.SetParent(container, false);
        var cursorRt = cursorObj.AddComponent<RectTransform>();
        cursorRt.anchorMin = new Vector2(0f, 0f);
        cursorRt.anchorMax = new Vector2(0f, 1f);
        cursorRt.pivot = new Vector2(0f, 0.5f);
        cursorRt.offsetMin = new Vector2(14f, 4f);
        cursorRt.offsetMax = new Vector2(18f, -4f);
        _cursorImg = cursorObj.AddComponent<Image>();
        _cursorImg.color = new Color(1f, 1f, 1f, 0.85f);
        _cursor = cursorObj.AddComponent<TextMeshProUGUI>();
        try
        {
            var f = VCUiKit.Font;
            if (f != null) _cursor.font = f;
        }
        catch { }
        _cursor.fontSize = fontSize;
        _cursor.color = Color.white;
        _cursor.alignment = TextAlignmentOptions.Left;
        _cursor.raycastTarget = false;
        _cursor.text = "";
        _cursor.enabled = false;

        _input = initial ?? "";
        _cursorPos = _input.Length;

        var btn = gameObject.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener((Action)(() => GainFocus()));

        UpdateDisplay();
    }

    public void GainFocus()
    {
        _active = this;
        _cursorPos = _input.Length;
        _cursor.enabled = true;
        _cursorTimer = 0.65f;
        Input.imeCompositionMode = IMECompositionMode.On;
        UpdateDisplay();
    }

    public void LoseFocus()
    {
        if (_active == this) _active = null;
        _cursor.enabled = false;
        Input.imeCompositionMode = IMECompositionMode.Off;
    }

    void Update()
    {
        if (_active != this) { _cursor.enabled = false; return; }

        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab))
        {
            LoseFocus();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            OnConfirm?.Invoke();
            LoseFocus();
            return;
        }

        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
        {
            if (Input.GetKeyDown(KeyCode.A)) { _selectBegin = 0; _cursorPos = _input.Length; }
            if (Input.GetKeyDown(KeyCode.C) && IsSelecting) CopySelection();
            if (Input.GetKeyDown(KeyCode.V)) { InsertText(GetClipboard()); _selectBegin = -1; }
            if (Input.GetKeyDown(KeyCode.X) && IsSelecting) { CopySelection(); DeleteSelection(); _selectBegin = -1; }
        }

        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            if (PressingShift && !IsSelecting) _selectBegin = _cursorPos;
            _cursorPos = Mathf.Max(0, _cursorPos - 1);
            if (!PressingShift) _selectBegin = -1;
            _cursorTimer = 0.65f;
        }
        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            if (PressingShift && !IsSelecting) _selectBegin = _cursorPos;
            _cursorPos = Mathf.Min(_input.Length, _cursorPos + 1);
            if (!PressingShift) _selectBegin = -1;
            _cursorTimer = 0.65f;
        }
        if (Input.GetKeyDown(KeyCode.Home))
        {
            if (PressingShift && !IsSelecting) _selectBegin = _cursorPos;
            _cursorPos = 0;
            if (!PressingShift) _selectBegin = -1;
            _cursorTimer = 0.65f;
        }
        if (Input.GetKeyDown(KeyCode.End))
        {
            if (PressingShift && !IsSelecting) _selectBegin = _cursorPos;
            _cursorPos = _input.Length;
            if (!PressingShift) _selectBegin = -1;
            _cursorTimer = 0.65f;
        }

        string inputString = Input.inputString;
        if (inputString.Length > 0)
        {
            foreach (char c in inputString)
            {
                if (c == '\b' || c == 0xFF)
                {
                    if (IsSelecting) DeleteSelection();
                    else if (_cursorPos > 0) { _input = _input.Remove(_cursorPos - 1, 1); _cursorPos--; }
                }
                else if (c >= 32 && c != '\r' && c != '\n')
                {
                    if (_input.Length >= _charLimit && !IsSelecting) continue;
                    if (IsSelecting) DeleteSelection();
                    _input = _input.Insert(_cursorPos, c.ToString());
                    _cursorPos++;
                }
            }
            _cursorTimer = 0.65f;
        }

        string comp = Input.compositionString;
        UpdateDisplay(comp);

        _cursorTimer -= Time.deltaTime;
        if (_cursorTimer < 0f)
        {
            _cursor.enabled = !_cursor.enabled;
            _cursorTimer = 0.65f;
        }
    }

    private void UpdateDisplay(string composition = "")
    {
        string display;
        if (_input.Length > 0 || composition.Length > 0)
        {
            display = _input;
        }
        else
        {
            display = _hint;
            _text.alpha = 0.45f;
            _text.text = display + " ";
            _text.ForceMeshUpdate();
            _cursor.enabled = false;
            OnValueChanged?.Invoke(_input);
            return;
        }

        _text.alpha = 1f;

        if (IsSelecting)
        {
            int min = Math.Min(_cursorPos, _selectBegin);
            int max = Math.Max(_cursorPos, _selectBegin);
            string before = display.Substring(0, min);
            string selected = display.Substring(min, max - min);
            string after = display.Substring(max);
            _text.text = $"<mark=#406090AA>{selected}</mark>{after}";
            display = before + selected + after;
        }

        _text.text = display + " ";
        _text.ForceMeshUpdate();

        int visCursor = _cursorPos;
        if (_text.textInfo.characterInfo.Length > 0 && visCursor <= _text.textInfo.characterInfo.Length)
        {
            int idx = Mathf.Clamp(visCursor, 0, _text.textInfo.characterInfo.Length - 1);
            float xPos = visCursor == 0 || _text.textInfo.characterInfo[idx].lineNumber !=
                (visCursor > 0 && visCursor - 1 < _text.textInfo.characterInfo.Length ?
                    _text.textInfo.characterInfo[visCursor - 1].lineNumber : -1)
                ? _text.rectTransform.rect.min.x
                : _text.textInfo.characterInfo[visCursor - 1].xAdvance;

            var lineInfo = _text.textInfo.lineInfo[_text.textInfo.characterInfo[idx].lineNumber];
            _cursor.transform.localPosition = new Vector3(xPos, lineInfo.baseline - 10f, -1f);
        }

        OnValueChanged?.Invoke(_input);
    }

    private void DeleteSelection()
    {
        int min = Math.Min(_cursorPos, _selectBegin);
        int count = Math.Abs(_cursorPos - _selectBegin);
        _input = _input.Remove(min, count);
        _cursorPos = min;
        _selectBegin = -1;
    }

    private void InsertText(string text)
    {
        if (_input.Length >= _charLimit) return;
        int space = _charLimit - _input.Length;
        if (text.Length > space) text = text.Substring(0, space);
        _input = _input.Insert(_cursorPos, text);
        _cursorPos += text.Length;
    }

    private void CopySelection()
    {
        if (!IsSelecting) return;
        int min = Math.Min(_cursorPos, _selectBegin);
        int count = Math.Abs(_cursorPos - _selectBegin);
        try { GUIUtility.systemCopyBuffer = _input.Substring(min, count); } catch { }
    }

    private static string GetClipboard()
    {
        try { return GUIUtility.systemCopyBuffer ?? ""; } catch { return ""; }
    }
}
