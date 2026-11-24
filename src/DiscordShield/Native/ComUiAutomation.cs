using System.Runtime.InteropServices;

namespace DiscordShield.Native;

/// <summary>
/// Lightweight native COM interop to Windows' built-in UIAutomationCore.dll (CLSID_CUIAutomation).
/// Eliminates the entire ~30 MB WPF runtime (&lt;UseWPF&gt;true&lt;/UseWPF&gt;) while providing the exact
/// same browser-tab reading and Upwork Stop/Pause accessibility invocation at 0 KB binary cost.
/// </summary>
public static class ComUiAutomation
{
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_ButtonControlTypeId = 50000;
    private const int UIA_CheckBoxControlTypeId = 50002;
    private const int UIA_TabItemControlTypeId = 50019;
    private const int UIA_InvokePatternId = 10000;
    private const int UIA_TogglePatternId = 10015;
    private const int TreeScope_Descendants = 4;
    private const int ToggleState_On = 1;

    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(IntPtr el1, IntPtr el2, out int areSame);
        void CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
        void GetRootElement(out IUIAutomationElement root);
        void ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        void ElementFromPoint(Point pt, out IUIAutomationElement element);
        void GetFocusedElement(out IUIAutomationElement element);
        void GetRootElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement root);
        void ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IUIAutomationElement element);
        void ElementFromPointBuildCache(Point pt, IntPtr cacheRequest, out IUIAutomationElement element);
        void GetFocusedElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement element);
        void CreateTreeWalker(IntPtr pCondition, out IntPtr walker);
        void get_ControlViewWalker(out IntPtr walker);
        void get_ContentViewWalker(out IntPtr walker);
        void get_RawViewWalker(out IntPtr walker);
        void get_RawViewCondition(out IntPtr condition);
        void get_ControlViewCondition(out IntPtr condition);
        void get_ContentViewCondition(out IntPtr condition);
        void CreateCacheRequest(out IntPtr cacheRequest);
        void CreateTrueCondition(out IntPtr newCondition);
        void CreateFalseCondition(out IntPtr newCondition);
        void CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IntPtr newCondition);
        void CreatePropertyConditionEx(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, int flags, out IntPtr newCondition);
        void CreateAndCondition(IntPtr condition1, IntPtr condition2, out IntPtr newCondition);
        void CreateAndConditionFromArray(IntPtr conditions, out IntPtr newCondition);
        void CreateAndConditionFromNativeArray(IntPtr conditions, int conditionCount, out IntPtr newCondition);
        void CreateOrCondition(IntPtr condition1, IntPtr condition2, out IntPtr newCondition);
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out IntPtr runtimeId);
        void FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
        void FindAll(int scope, IntPtr condition, out IUIAutomationElementArray found);
        void FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement found);
        void FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElementArray found);
        void BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement updatedElement);
        void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        void GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        void GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        void GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object retVal);
        void GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        void GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        void GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? patternObject);
        void GetCachedPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? patternObject);
        void GetCachedParent(out IUIAutomationElement parent);
        void GetCachedChildren(out IUIAutomationElementArray children);
        int CurrentProcessId { get; }
        int CurrentControlType { get; }
        string CurrentLocalizedControlType { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string CurrentName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    }

    [ComImport]
    [Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        void GetElement(int index, out IUIAutomationElement element);
    }

    [ComImport]
    [Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationInvokePattern
    {
        void Invoke();
    }

    [ComImport]
    [Guid("94CF8058-9B8D-4AB9-8BFD-4CD0A33C8C70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTogglePattern
    {
        void Toggle();
        int CurrentToggleState { get; }
    }

    private static IUIAutomation? CreateAutomation()
    {
        try
        {
            Type? type = Type.GetTypeFromCLSID(ClsidCUIAutomation);
            return type is not null ? Activator.CreateInstance(type) as IUIAutomation : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SafeReleaseCom(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            _ = Marshal.ReleaseComObject(comObj);
        }
    }

    public static List<string> ReadTabItemNames(IntPtr hwnd, int maxTabs = 40)
    {
        var results = new List<string>();
        if (hwnd == IntPtr.Zero) return results;

        IUIAutomation? uia = CreateAutomation();
        if (uia is null) return results;

        IntPtr cond = IntPtr.Zero;
        IUIAutomationElement? root = null;
        IUIAutomationElementArray? found = null;
        try
        {
            uia.ElementFromHandle(hwnd, out root);
            if (root is null) return results;

            uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_TabItemControlTypeId, out cond);
            root.FindAll(TreeScope_Descendants, cond, out found);
            if (found is null) return results;

            int count = Math.Min(found.Length, maxTabs);
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement? el = null;
                try
                {
                    found.GetElement(i, out el);
                    string? name = el?.CurrentName;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        results.Add(name);
                    }
                }
                finally
                {
                    SafeReleaseCom(el);
                }
            }
        }
        catch
        {
            // Browser UIA tree may be busy or elevated.
        }
        finally
        {
            SafeReleaseCom(found);
            SafeReleaseCom(root);
            if (cond != IntPtr.Zero) Marshal.Release(cond);
            SafeReleaseCom(uia);
        }

        return results;
    }

    public static (bool Invoked, string Message) TryInvokeMatchingControl(IntPtr hwnd, Func<string, bool> namePredicate)
    {
        if (hwnd == IntPtr.Zero) return (false, "Invalid window handle.");

        IUIAutomation? uia = CreateAutomation();
        if (uia is null) return (false, "Windows UI Automation unavailable.");

        IntPtr btnCond = IntPtr.Zero;
        IntPtr chkCond = IntPtr.Zero;
        IntPtr orCond = IntPtr.Zero;
        IUIAutomationElement? root = null;
        IUIAutomationElementArray? found = null;
        try
        {
            uia.ElementFromHandle(hwnd, out root);
            if (root is null) return (false, "Could not inspect window.");

            uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_ButtonControlTypeId, out btnCond);
            uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_CheckBoxControlTypeId, out chkCond);
            uia.CreateOrCondition(btnCond, chkCond, out orCond);

            root.FindAll(TreeScope_Descendants, orCond, out found);
            if (found is null) return (false, "No controls found.");

            int count = found.Length;
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement? el = null;
                try
                {
                    found.GetElement(i, out el);
                    if (el is null) continue;

                    string name = el.CurrentName ?? string.Empty;
                    if (!namePredicate(name)) continue;

                    object? toggleObj = null;
                    try
                    {
                        el.GetCurrentPattern(UIA_TogglePatternId, out toggleObj);
                        if (toggleObj is IUIAutomationTogglePattern toggle && toggle.CurrentToggleState == ToggleState_On)
                        {
                            toggle.Toggle();
                            return (true, $"Toggled Upwork control '{name}' off via UI Automation.");
                        }
                    }
                    finally
                    {
                        SafeReleaseCom(toggleObj);
                    }

                    object? invokeObj = null;
                    try
                    {
                        el.GetCurrentPattern(UIA_InvokePatternId, out invokeObj);
                        if (invokeObj is IUIAutomationInvokePattern invoke)
                        {
                            invoke.Invoke();
                            return (true, $"Invoked Upwork control '{name}' via UI Automation.");
                        }
                    }
                    finally
                    {
                        SafeReleaseCom(invokeObj);
                    }
                }
                finally
                {
                    SafeReleaseCom(el);
                }
            }

            return (false, "Upwork window found, but no active Stop/Pause button matched.");
        }
        catch (Exception ex)
        {
            return (false, $"UI Automation could not inspect Upwork window: {ex.Message}");
        }
        finally
        {
            SafeReleaseCom(found);
            SafeReleaseCom(root);
            if (orCond != IntPtr.Zero) Marshal.Release(orCond);
            if (chkCond != IntPtr.Zero) Marshal.Release(chkCond);
            if (btnCond != IntPtr.Zero) Marshal.Release(btnCond);
            SafeReleaseCom(uia);
        }
    }
}
