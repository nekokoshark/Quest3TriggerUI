using UnityEngine;
namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        private static ExpressionBrowserPanel _expressionDock;
        internal static bool ExpressionDockEditorAvailable
        {get{return _presetList!=null&&_presetList.activeInHierarchy&&SuperController.singleton!=null&&SuperController.singleton.MainHUDVisible;}}
        internal static void OpenExpressionDock(ExpressionBrowserPanel browser)
        {
            _expressionDock=browser;
            SetDockMode(3);
            browser.RevealDock();
            TickExpressionDock();
        }
        internal static void DetachExpressionDock(ExpressionBrowserPanel browser)
        {if(object.ReferenceEquals(browser,_expressionDock))_expressionDock=null;}
        internal static void SelectExpressionDockMode(int mode){SetDockMode(mode);}
        private static void TickExpressionDock()
        {
            if(_expressionDock==null)return;
            var sc=SuperController.singleton;
            var list=_presetList==null?null:_presetList.transform as RectTransform;
            bool visible=_dockMode==3 && list!=null && list.gameObject.activeInHierarchy && sc!=null &&
                !sc.isLoading && sc.MainHUDVisible && !_presetBrowsing;
            _expressionDock.BindEditorDock(list,visible);
        }
    }
}
