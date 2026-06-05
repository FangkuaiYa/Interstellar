using HarmonyLib;
using TMPro;
using UnityEngine;

namespace VoiceChatPlugin;

/*internal class CreateGameOptionsPatch
{
    public static GameObject ModServerOption;

	[HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Show))]
    static class CreateGameOptionsOpenShowPatch
    {
        static void Postfix(CreateGameOptions __instance)
        {
        }
    }

    [HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Start))]
    public static class CreateGameOptionsStartPatch
    {
        private static void Postfix(CreateGameOptions __instance)
        {
            var serverOption = GameObject.Find("ServerOption");
			ModServerOption = UnityEngine.Object.Instantiate(serverOption, serverOption.transform.parent);
            ModServerOption.transform.localPosition = new Vector3(serverOption.transform.localPosition.x, serverOption.transform.localPosition.y - 0.5f, serverOption.transform.localPosition.z);
			var optionTitleText = ModServerOption.transform.GetChild(0).GetChild(0);
			GameObject.Destroy(optionTitleText.GetComponent<TextTranslatorTMP>());
			optionTitleText.GetComponent<TextMeshPro>().text = "VC Server";
			var optionServerTextInactive = ModServerOption.transform.GetChild(1).GetChild(1).GetChild(0);
			var optionServerTextActive = ModServerOption.transform.GetChild(1).GetChild(1).GetChild(1);
			GameObject.Destroy(optionServerTextInactive.GetComponent<TextTranslatorTMP>());
			GameObject.Destroy(optionServerTextActive.GetComponent<TextTranslatorTMP>());
			optionServerTextInactive.GetComponent<TextMeshPro>().text = "Default";
			optionServerTextActive.GetComponent<TextMeshPro>().text = "Default";
            var serverListButton = ModServerOption.transform.GetChild(1).GetChild(1);
			// serverListButton是点击以后显示服务器列表的按钮的GameObject，直接GetComponent<PassiveButton>()即可取得PassiveButton，剩下的需要你写
		}
    }
}*/
