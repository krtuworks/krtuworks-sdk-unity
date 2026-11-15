#nullable enable
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Krtuworks.EditorTools
{
    /// <summary>
    /// What a build needs from the SDK: the app's krtuworks-config.json in a Resources folder (checked before
    /// the build starts), on Android the app's scheme on its main activity (the sign-in page comes back
    /// through it), on iOS the system frameworks the sign-in session and the Keychain use.
    /// </summary>
    public sealed class KrtuworksBuildSteps : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        internal static KrtuworksAppConfig Config()
        {
            var asset = Resources.Load<TextAsset>("krtuworks-config");
            if (asset == null) throw new BuildFailedException("Krtuworks SDK: put the app's krtuworks-config.json (from the Credentials page) in a Resources folder, as Assets/Resources/krtuworks-config.json.");
            var config = KrtuworksAppConfig.FromJson(asset.text);
            if (string.IsNullOrEmpty(config.AppId) || string.IsNullOrEmpty(config.Slug)) throw new BuildFailedException("Krtuworks SDK: krtuworks-config.json has no appId or slug; download it again from the Credentials page.");
            return config;
        }

        public void OnPreprocessBuild(BuildReport report) => Config();
    }

#if UNITY_ANDROID
    public sealed class KrtuworksAndroidBuild : UnityEditor.Android.IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 0;

        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        /// <summary>
        /// Adds `com.krtuworks.&lt;slug&gt;` as a browsable deep link to the launcher activity (Unity's activity is
        /// single-task: the link comes back to the running game), and keeps the SDK's Java class through R8
        /// (it is reached only by name, through JNI).
        /// </summary>
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var scheme = $"com.krtuworks.{KrtuworksBuildSteps.Config().Slug}";
            // The launcher activity is in the unityLibrary module (2022.3) or in the launcher module.
            var manifests = new[] { Path.Combine(path, "src", "main", "AndroidManifest.xml"), Path.Combine(path, "..", "launcher", "src", "main", "AndroidManifest.xml") };
            if (!manifests.Where(File.Exists).Any(m => AddScheme(m, scheme)))
                throw new BuildFailedException("Krtuworks SDK: no launcher activity in the generated Android manifests to receive the sign-in.");
            KeepJavaClass(Path.Combine(path, "proguard-unity.txt"));
        }

        static bool AddScheme(string manifestPath, string scheme)
        {
            var manifest = new XmlDocument();
            manifest.Load(manifestPath);
            var names = new XmlNamespaceManager(manifest.NameTable);
            names.AddNamespace("android", AndroidNs);
            var launcher = manifest.SelectNodes("/manifest/application/activity", names)!.Cast<XmlElement>()
                .FirstOrDefault(a => a.SelectSingleNode("intent-filter/category[@android:name='android.intent.category.LAUNCHER']", names) != null);
            if (launcher == null) return false;
            if (launcher.SelectSingleNode($"intent-filter/data[@android:scheme='{scheme}']", names) != null) return true;
            var filter = manifest.CreateElement("intent-filter");
            void Child(string tag, string name, string value)
            {
                var element = manifest.CreateElement(tag);
                element.SetAttribute(name, AndroidNs, value);
                filter.AppendChild(element);
            }
            Child("action", "name", "android.intent.action.VIEW");
            Child("category", "name", "android.intent.category.DEFAULT");
            Child("category", "name", "android.intent.category.BROWSABLE");
            Child("data", "scheme", scheme);
            launcher.AppendChild(filter);
            manifest.Save(manifestPath);
            return true;
        }

        static void KeepJavaClass(string rulesPath)
        {
            const string rule = "-keep class com.krtuworks.sdk.KrtuworksKeystore { public static *; }";
            var existing = File.Exists(rulesPath) ? File.ReadAllText(rulesPath) : "";
            if (!existing.Contains(rule)) File.AppendAllText(rulesPath, $"\n{rule}\n");
        }
    }
#endif

#if UNITY_IOS
    public static class KrtuworksIosBuild
    {
        [UnityEditor.Callbacks.PostProcessBuild(100)]
        public static void LinkFrameworks(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            var projectPath = UnityEditor.iOS.Xcode.PBXProject.GetPBXProjectPath(path);
            var project = new UnityEditor.iOS.Xcode.PBXProject();
            project.ReadFromFile(projectPath);
            var framework = project.GetUnityFrameworkTargetGuid();
            project.AddFrameworkToProject(framework, "AuthenticationServices.framework", false);
            project.AddFrameworkToProject(framework, "Security.framework", false);
            project.WriteToFile(projectPath);
        }
    }
#endif
}
