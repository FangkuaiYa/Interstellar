#pragma warning disable CS8602, CS8603
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

public static class VCTextInputPopup
{
    private static GameObject? _popup;
    private static TMP_InputField? _input;
    private static Action<string>? _onSave;

    public static bool IsShowing => _popup != null && _popup.activeSelf;

    public static void Show(string title, string placeholder, string current, int charLimit, Action<string> onSave)
    {
        Hide();
        _onSave = onSave;

        var canvas = VCUiKit.EnsureCanvas();
        _popup = new GameObject("VC_InputPopup");
        _popup.transform.SetParent(canvas.transform, false);
        var rt = _popup.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var dim = VCUiKit.CreateImage(_popup.transform, "Dim", Vector2.zero, Vector2.zero, VCUiKit.PixelSprite, new Color(0f, 0f, 0f, 0.45f));
        var dimRt = (RectTransform)dim.transform;
        dimRt.anchorMin = Vector2.zero;
        dimRt.anchorMax = Vector2.one;
        dimRt.offsetMin = Vector2.zero;
        dimRt.offsetMax = Vector2.zero;

        const float pw = 720f, ph = 330f;
        var panel = VCUiKit.CreatePanel(_popup.transform, "Panel", new Vector2(pw, ph),
            new Color(0.88f, 0.94f, 1f, 1f), new Color(0.07f, 0.10f, 0.16f, 0.97f));

        VCUiKit.CreateText(panel, "Title", title, new Vector2(0f, ph * 0.5f - 46f), new Vector2(pw - 80f, 44f),
            30f, new Color(0.92f, 0.95f, 1f, 1f), FontStyles.Bold, TextAlignmentOptions.Center);

        _input = VCUiKit.CreateTextInput(panel, "Input", new Vector2(0f, 6f), new Vector2(pw - 120f, 62f),
            new Color(0.10f, 0.13f, 0.20f, 1f), placeholder, 26f, charLimit);
        _input.text = current ?? "";

        var feeder = _input.gameObject.AddComponent<VCInputFeeder>();
        feeder.Bind(_input);

        VCUiKit.CreateButton(panel, TranslationHelper.Get("vc.popup.confirm", "Confirm"), new Vector2(-130f, -118f), new Vector2(200f, 58f),
            new Color(0.20f, 0.62f, 0.33f, 1f), () => Confirm(), 26f);
        VCUiKit.CreateButton(panel, TranslationHelper.Get("vc.popup.cancel", "Cancel"), new Vector2(130f, -118f), new Vector2(200f, 58f),
            new Color(0.65f, 0.25f, 0.25f, 1f), () => Hide(), 26f);

        feeder.Focus();
    }

    public static void Confirm()
    {
        if (_input == null) { Hide(); return; }
        var save = _onSave;
        var text = _input.text;
        Hide();
        save?.Invoke(text);
    }

    public static void Hide()
    {
        if (_popup != null) Object.Destroy(_popup);
        _popup = null;
        _input = null;
        _onSave = null;
    }
}

public class VCInputFeeder : MonoBehaviour
{
    private TMP_InputField? _field;
    private bool _focused;
    private string _buf = "";

    private static readonly Dictionary<KeyCode, char> _shiftMap = new()
    {
        { KeyCode.Alpha1, '!' }, { KeyCode.Alpha2, '@' }, { KeyCode.Alpha3, '#' },
        { KeyCode.Alpha4, '$' }, { KeyCode.Alpha5, '%' }, { KeyCode.Alpha6, '^' },
        { KeyCode.Alpha7, '&' }, { KeyCode.Alpha8, '*' }, { KeyCode.Alpha9, '(' },
        { KeyCode.Alpha0, ')' }, { KeyCode.Minus, '_' }, { KeyCode.Equals, '+' },
        { KeyCode.LeftBracket, '{' }, { KeyCode.RightBracket, '}' },
        { KeyCode.Backslash, '|' }, { KeyCode.Semicolon, ':' }, { KeyCode.Quote, '"' },
        { KeyCode.Comma, '<' }, { KeyCode.Period, '>' }, { KeyCode.Slash, '?' },
        { KeyCode.BackQuote, '~' },
    };

    public VCInputFeeder(IntPtr ptr) : base(ptr) { }

    public void Bind(TMP_InputField field) => _field = field;

    public void Focus()
    {
        _focused = true;
        _buf = "";
        if (_field != null)
        {
            _field.ActivateInputField();
            _field.Select();
        }
    }

    void Update()
    {
        if (_field == null || !VCTextInputPopup.IsShowing) return;

        if (!_focused)
        {
            if (Input.GetMouseButtonDown(0))
            {
                if (_field.textViewport != null &&
                    RectTransformUtility.RectangleContainsScreenPoint(
                        _field.textViewport, Input.mousePosition, (Camera?)null))
                {
                    Focus();
                }
            }
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab))
        {
            _focused = false;
            _field.DeactivateInputField();
            VCTextInputPopup.Hide();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            VCTextInputPopup.Confirm();
            return;
        }

        if (!_field.isFocused)
        {
            _field.ActivateInputField();
        }

        _buf = Input.inputString;

        if (Input.GetKeyDown(KeyCode.Backspace))
        {
            if (_field.selectionAnchorPosition != _field.selectionFocusPosition)
            {
                int min = Mathf.Min(_field.selectionAnchorPosition, _field.selectionFocusPosition);
                int max = Mathf.Max(_field.selectionAnchorPosition, _field.selectionFocusPosition);
                _field.text = _field.text.Remove(min, max - min);
                _field.caretPosition = min;
            }
            else if (_field.caretPosition > 0)
            {
                _field.text = _field.text.Remove(_field.caretPosition - 1, 1);
                _field.caretPosition--;
            }
            _field.ForceLabelUpdate();
            return;
        }

        if (_buf.Length > 0)
        {
            foreach (char c in _buf)
            {
                if (c == '\b') continue;
                if (c >= 32 && c != '\r' && c != '\n')
                {
                    if (_field.characterLimit > 0 && _field.text.Length >= _field.characterLimit) continue;
                    int pos = Mathf.Min(_field.selectionAnchorPosition, _field.selectionFocusPosition);
                    int selLen = Mathf.Abs(_field.selectionFocusPosition - _field.selectionAnchorPosition);
                    if (selLen > 0) _field.text = _field.text.Remove(pos, selLen);
                    _field.text = _field.text.Insert(pos, c.ToString());
                    _field.caretPosition = pos + 1;
                }
            }
            _field.ForceLabelUpdate();
            return;
        }

        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool caps = Input.GetKey(KeyCode.CapsLock);

        for (int i = (int)KeyCode.A; i <= (int)KeyCode.Z; i++)
        {
            if (!Input.GetKeyDown((KeyCode)i)) continue;
            char ch = (char)('a' + (i - (int)KeyCode.A));
            if (shift ^ caps) ch = char.ToUpper(ch);
            InsertChar(ch);
            return;
        }

        for (int i = (int)KeyCode.Alpha0; i <= (int)KeyCode.Alpha9; i++)
        {
            if (!Input.GetKeyDown((KeyCode)i)) continue;
            char ch = shift && _shiftMap.TryGetValue((KeyCode)i, out var sc) ? sc : (char)('0' + (i - (int)KeyCode.Alpha0));
            InsertChar(ch);
            return;
        }

        if (Input.GetKeyDown(KeyCode.Space)) { InsertChar(' '); return; }
        if (Input.GetKeyDown(KeyCode.Period)) { InsertChar(shift ? '>' : '.'); return; }
        if (Input.GetKeyDown(KeyCode.Comma)) { InsertChar(shift ? '<' : ','); return; }
        if (Input.GetKeyDown(KeyCode.Slash)) { InsertChar(shift ? '?' : '/'); return; }
        if (Input.GetKeyDown(KeyCode.Backslash)) { InsertChar(shift ? '|' : '\\'); return; }
        if (Input.GetKeyDown(KeyCode.Semicolon)) { InsertChar(shift ? ':' : ';'); return; }
        if (Input.GetKeyDown(KeyCode.Quote)) { InsertChar(shift ? '"' : '\''); return; }
        if (Input.GetKeyDown(KeyCode.LeftBracket)) { InsertChar(shift ? '{' : '['); return; }
        if (Input.GetKeyDown(KeyCode.RightBracket)) { InsertChar(shift ? '}' : ']'); return; }
        if (Input.GetKeyDown(KeyCode.Minus)) { InsertChar(shift ? '_' : '-'); return; }
        if (Input.GetKeyDown(KeyCode.Equals)) { InsertChar(shift ? '+' : '='); return; }
        if (Input.GetKeyDown(KeyCode.BackQuote)) { InsertChar(shift ? '~' : '`'); return; }
    }

    private void InsertChar(char c)
    {
        if (_field == null) return;
        if (_field.characterLimit > 0 && _field.text.Length >= _field.characterLimit) return;
        int pos = Mathf.Min(_field.selectionAnchorPosition, _field.selectionFocusPosition);
        int selLen = Mathf.Abs(_field.selectionFocusPosition - _field.selectionAnchorPosition);
        if (selLen > 0) _field.text = _field.text.Remove(pos, selLen);
        _field.text = _field.text.Insert(pos, c.ToString());
        _field.caretPosition = pos + 1;
        _field.ForceLabelUpdate();
    }
}
