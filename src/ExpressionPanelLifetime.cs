using UnityEngine;
namespace Quest3TriggerUI {
 internal sealed class ExpressionPanelLifetime : MonoBehaviour {
  internal ExpressionBrowserPanel Owner;
  private void Update(){if(Owner!=null)Owner.ObservePlaybackLifetime();}
 }
}
