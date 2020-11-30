using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

using Microsoft.DotNet.XHarness.iOS.Shared.Execution;
using Microsoft.DotNet.XHarness.iOS.Shared.Logging;

namespace Microsoft.DotNet.XHarness.iOS.Shared.Utilities {
	public static class ProjectFileExtensions {
		const string MSBuild_Namespace = "http://schemas.microsoft.com/developer/msbuild/2003";

		public static void SetProjectTypeGuids (this XmlDocument csproj, string value)
		{
			SetNode (csproj, "ProjectTypeGuids", value);
		}

		public static string GetProjectGuid (this XmlDocument csproj)
		{
			return csproj.SelectSingleNode ("/*/*/*[local-name() = 'ProjectGuid']").InnerText;
		}

		public static void SetProjectGuid (this XmlDocument csproj, string value)
		{
			csproj.SelectSingleNode ("/*/*/*[local-name() = 'ProjectGuid']").InnerText = value;
		}

		public static string GetOutputType (this XmlDocument csproj)
		{
			return csproj.SelectSingleNode ("/*/*/*[local-name() = 'OutputType']").InnerText;
		}

		public static void SetOutputType (this XmlDocument csproj, string value)
		{
			csproj.SelectSingleNode ("/*/*/*[local-name() = 'OutputType']").InnerText = value;
		}

		static string[] eqsplitter = new string[] { "==" };
		static string[] orsplitter = new string[] { " Or " };
		static char[] pipesplitter = new char[] { '|' };
		static char[] trimchars = new char[] { '\'', ' ' };

		static void ParseConditions (this XmlNode node, out string platform, out string configuration)
		{
			// This parses the platform/configuration out of conditions like this:
			//
			// Condition=" '$(Configuration)|$(Platform)' == 'Debug|iPhoneSimulator' "
			//
			platform = "Any CPU";
			configuration = "Debug";
			while (node != null) {
				if (node.Attributes != null) {
					var conditionAttribute = node.Attributes ["Condition"];
					if (conditionAttribute != null) {
						var condition = conditionAttribute.Value;
						var eqsplit = condition.Split (eqsplitter, StringSplitOptions.None);
						if (eqsplit.Length == 2) {
							var left = eqsplit [0].Trim (trimchars).Split (pipesplitter);
							var right = eqsplit [1].Trim (trimchars).Split (pipesplitter);
							if (left.Length == right.Length) {
								for (int i = 0; i < left.Length; i++) {
									switch (left [i]) {
									case "$(Configuration)":
										configuration = right [i];
										break;
									case "$(Platform)":
										platform = right [i];
										break;
									default:
										throw new Exception (string.Format ("Unknown condition logic: {0}", left [i]));
									}
								}
							}
						}

						if (string.IsNullOrEmpty (platform) || string.IsNullOrEmpty (configuration))
							throw new Exception (string.Format ("Could not parse the condition: {0}", conditionAttribute.Value));
					}
				}
				node = node.ParentNode;
			}

			if (string.IsNullOrEmpty (platform) || string.IsNullOrEmpty (configuration))
				throw new Exception ("Could not find a condition attribute.");
		}

		public static void SetOutputPath (this XmlDocument csproj, string value, bool expand = true)
		{
			var nodes = csproj.SelectNodes ("/*/*/*[local-name() = 'OutputPath']");
			if (nodes.Count == 0)
				throw new Exception ("Could not find node OutputPath");
			foreach (XmlNode n in nodes) {
				if (expand) {
					// OutputPath needs to be expanded, otherwise Xamarin Studio isn't able to launch the project.
					string platform, configuration;
					ParseConditions (n, out platform, out configuration);
					n.InnerText = value.Replace ("$(Platform)", platform).Replace ("$(Configuration)", configuration);
				} else {
					n.InnerText = value;
				}
			}
		}

		static bool IsNodeApplicable (XmlNode node, string platform, string configuration)
		{
			while (node != null) {
				if (!EvaluateCondition (node, platform, configuration))
					return false;
				node = node.ParentNode;
			}
			return true;
		}

		static bool EvaluateCondition (XmlNode node, string platform, string configuration)
		{
			if (node.Attributes == null)
				return true;
			
			var condition = node.Attributes ["Condition"];
			if (condition == null)
				return true;
				
			var conditionValue = condition.Value;
			if (configuration != null)
				conditionValue = conditionValue.Replace ("$(Configuration)", configuration);
			if (platform != null)
				conditionValue = conditionValue.Replace ("$(Platform)", platform);

			var orsplits = conditionValue.Split (orsplitter, StringSplitOptions.None);
			foreach (var orsplit in orsplits) {
				var eqsplit = orsplit.Split (eqsplitter, StringSplitOptions.None);
				if (eqsplit.Length != 2) {
					Console.WriteLine ("Could not parse condition; {0}", conditionValue);
					return false;
				}

				var left = eqsplit [0].Trim (trimchars);
				var right = eqsplit [1].Trim (trimchars);
				if (left == right)
					return true;
			}

			return false;
		}

		public static string GetOutputPath (this XmlDocument csproj, string platform, string configuration)
		{
			return GetElementValue (csproj, platform, configuration, "OutputPath");
		}

		static string GetElementValue (this XmlDocument csproj, string platform, string configuration, string elementName, bool throwIfNotFound = true)
		{
			var nodes = csproj.SelectNodes ($"/*/*/*[local-name() = '{elementName}']");
			if (nodes.Count == 0) {
				if (throwIfNotFound)
					throw new Exception ($"Could not find node {elementName}");
				return null;
			}
			foreach (XmlNode n in nodes) {
				if (IsNodeApplicable (n, platform, configuration))
					return n.InnerText.Replace ("$(Platform)", platform).Replace ("$(Configuration)", configuration);
			}
			throw new Exception ($"Could not find {elementName}");
		}

		public static string GetOutputAssemblyPath (this XmlDocument csproj, string platform, string configuration)
		{
			var outputPath = GetOutputPath (csproj, platform, configuration);
			var assemblyName = GetElementValue (csproj, platform, configuration, "AssemblyName");
			var outputType = GetElementValue (csproj, platform, configuration, "OutputType");
			string extension;
			switch (outputType.ToLowerInvariant ()) {
			case "library":
				extension = "dll";
				break;
			case "exe":
				extension = "exe";
				break;
			default:
				throw new NotImplementedException (outputType);
			}
			return outputPath + "\\" + assemblyName + "." + extension; // MSBuild-style paths.
		}

		public static string GetIsBindingProject (this XmlDocument csproj)
		{
			return GetElementValue (csproj, string.Empty, string.Empty, "IsBindingProject", throwIfNotFound: false);
		}

		public static void SetIntermediateOutputPath (this XmlDocument csproj, string value)
		{
			// Set any existing IntermediateOutputPath
			var nodes = csproj.SelectNodes ("/*/*/*[local-name() = 'IntermediateOutputPath']");
			var hasToplevel = false;
			if (nodes.Count != 0) {
				foreach (XmlNode n in nodes) {
					n.InnerText = value;
					hasToplevel |= n.Attributes ["Condition"] == null;
				}
			}

			if (hasToplevel)
				return;
			
			// Make sure there's a top-level version too.
			var property_group = csproj.SelectSingleNode("/*/*[local-name() = 'PropertyGroup' and not(@Condition)]");

			var intermediateOutputPath = csproj.CreateElement ("IntermediateOutputPath", csproj.GetNamespace ());
			intermediateOutputPath.InnerText = value;
			property_group.AppendChild (intermediateOutputPath);
		}

		public static void SetTargetFrameworkIdentifier (this XmlDocument csproj, string value)
		{
			SetTopLevelPropertyGroupValue (csproj, "TargetFrameworkIdentifier", value);
		}

		public static void SetTopLevelPropertyGroupValue (this XmlDocument csproj, string key, string value)
		{
			var firstPropertyGroups = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup']")[0];
			var targetFrameworkIdentifierNode = firstPropertyGroups.SelectSingleNode (string.Format ("//*[local-name() = '{0}']", key));
			if (targetFrameworkIdentifierNode != null)
			{
				SetNode (csproj, key, value);
			}
			else
			{
				var mea = csproj.CreateElement (key, csproj.GetNamespace ());
				mea.InnerText = value;
				firstPropertyGroups.AppendChild (mea);
			}
 		}

		public static void RemoveTargetFrameworkIdentifier (this XmlDocument csproj)
		{
			RemoveNode (csproj, "TargetFrameworkIdentifier", throwOnInexistentNode: false);
		}

		public static void SetAssemblyName (this XmlDocument csproj, string value)
		{
			SetNode (csproj, "AssemblyName", value);
		}

		public static string GetAssemblyName (this XmlDocument csproj)
		{
			var assemblyNameNode = csproj.SelectSingleNode ("/*/*/*[local-name() = 'AssemblyName']");
			if (assemblyNameNode != null)
				return assemblyNameNode.InnerText;
			return Path.GetFileNameWithoutExtension (csproj.GetFilename ());
		}

		public static void SetPlatformAssembly (this XmlDocument csproj, string value)
		{
			SetAssemblyReference (csproj, "Xamarin.iOS", value);
		}

		public static void SetAssemblyReference (this XmlDocument csproj, string current, string value)
		{
			var project = csproj.ChildNodes [1];
			var reference = csproj.SelectSingleNode ("/*/*/*[local-name() = 'Reference' and @Include = '" + current + "']");
			if (reference != null)
				reference.Attributes ["Include"].Value = value;
		}

		public static void RemoveReferences (this XmlDocument csproj, string projectName)
		{
			var reference = csproj.SelectSingleNode ("/*/*/*[local-name() = 'Reference' and @Include = '" + projectName + "']");
			if (reference != null)
				reference.ParentNode.RemoveChild (reference);
		}

		public static void RemovePackageReference (this XmlDocument csproj, string projectName)
		{
			var reference = csproj.SelectSingleNode ("/*/*/*[local-name() = 'PackageReference' and @Include = '" + projectName + "']");
			if (reference != null)
				reference.ParentNode.RemoveChild (reference);
		}

		public static void AddCompileInclude (this XmlDocument csproj, string link, string include, bool prepend = false)
		{
			AddInclude (csproj, "Compile", link, include, prepend);
		}

		public static void AddInclude (this XmlDocument csproj, string type, string link, string include, bool prepend = false)
		{
			var type_node = csproj.SelectSingleNode ($"//*[local-name() = '{type}']");
			var item_group = type_node?.ParentNode ?? csproj.SelectSingleNode ($"//*[local-name() = 'ItemGroup'][last()]");

			var node = csproj.CreateElement (type, csproj.GetNamespace ());
			var include_attribute = csproj.CreateAttribute ("Include");
			include_attribute.Value = include;
			node.Attributes.Append (include_attribute);
			var linkElement = csproj.CreateElement ("Link", csproj.GetNamespace ());
			linkElement.InnerText = link;
			node.AppendChild (linkElement);
			if (prepend)
				item_group.PrependChild (node);
			else 
				item_group.AppendChild (node);
		}

		public static void FixCompileInclude (this XmlDocument csproj, string include, string newInclude)
		{
			csproj.SelectSingleNode ($"//*[local-name() = 'Compile' and @Include = '{include}']").Attributes ["Include"].Value = newInclude;
		}

		public static void AddInterfaceDefinition (this XmlDocument csproj, string include)
		{
			var itemGroup = csproj.CreateItemGroup ();
			var id = csproj.CreateElement ("InterfaceDefinition", csproj.GetNamespace ());
			var attrib = csproj.CreateAttribute ("Include");
			attrib.Value = include;
			id.Attributes.Append (attrib);
			itemGroup.AppendChild (id);
		}

		public static void SetImport (this XmlDocument csproj, string value)
		{
			var import = GetImport (csproj);
			if (string.IsNullOrEmpty (import))
				throw new Exception ($"Could not find the xamarin import");
			var imports = csproj.SelectNodes ($"/*/*[local-name() = 'Import'][@Project = '{import}']");			
			if (imports.Count != 1)
				throw new Exception ($"Found {imports.Count} xamarin imports?");
			imports [0].Attributes ["Project"].Value = value;
		}

		public static void SetExtraLinkerDefs (this XmlDocument csproj, string value)
		{
			var mtouchExtraArgs = csproj.SelectNodes ("//*[local-name() = 'MtouchExtraArgs']");
			foreach (XmlNode mea in mtouchExtraArgs)
				mea.InnerText = mea.InnerText.Replace ("extra-linker-defs.xml", value);
			var nones = csproj.SelectNodes ("//*[local-name() = 'None' and @Include = 'extra-linker-defs.xml']");
			foreach (XmlNode none in nones)
				none.Attributes ["Include"].Value = value;
		}

		public static void AddExtraMtouchArgs (this XmlDocument csproj, string value, string platform, string configuration)
		{
			AddToNode (csproj, "MtouchExtraArgs", value, platform, configuration);
		}

		public static void AddMonoBundlingExtraArgs (this XmlDocument csproj, string value, string platform, string configuration)
		{
			AddToNode (csproj, "MonoBundlingExtraArgs", value, platform, configuration);
		}

		public static void AddToNode (this XmlDocument csproj, string node, string value, string platform, string configuration)
		{
			var nodes = csproj.SelectNodes ($"//*[local-name() = '{node}']");
			foreach (XmlNode mea in nodes) {
				if (!IsNodeApplicable (mea, platform, configuration))
					continue;

				if (mea.InnerText.Length > 0 && mea.InnerText [mea.InnerText.Length - 1] != ' ')
					mea.InnerText += " ";
				mea.InnerText += value;
				return;
			}

			// The project might not have this node, so create one of none was found.
			var propertyGroups = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup' and @Condition]").Cast<XmlNode> ();
			var propertyGroup = propertyGroups.FirstOrDefault (v => EvaluateCondition (v, platform, configuration));
			if (propertyGroup == null)
				propertyGroup = csproj.AddPropertyGroup (platform, configuration);

			var newNode = csproj.CreateElement (node, csproj.GetNamespace ());
			newNode.InnerText = value;
			propertyGroup.AppendChild (newNode);
		}

		public static string GetMtouchLink (this XmlDocument csproj, string platform, string configuration)
		{
			return GetNode (csproj, "MtouchLink", platform, configuration);
		}

		public static void SetMtouchUseLlvm (this XmlDocument csproj, bool value, string platform, string configuration)
		{
			SetNode (csproj, "MtouchUseLlvm", true ? "true" : "false", platform, configuration);
		}

		public static void SetMtouchUseBitcode (this XmlDocument csproj, bool value, string platform, string configuration)
		{
			SetNode (csproj, "MtouchEnableBitcode", true ? "true" : "false", platform, configuration);
		}

		public static IEnumerable<XmlNode> GetPropertyGroups (this XmlDocument csproj, string platform, string configuration)
		{
			var propertyGroups = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup' and @Condition]");
			foreach (XmlNode node in propertyGroups) {
				if (!EvaluateCondition (node, platform, configuration))
					continue;

				yield return node;
			}
		}

		public static void SetNode (this XmlDocument csproj, string node, string value, string platform, string configuration)
		{
			var projnode = csproj.SelectElementNodes (node);
			var found = false;
			foreach (XmlNode xmlnode in projnode) {
				if (!IsNodeApplicable (xmlnode, platform, configuration))
					continue;

				xmlnode.InnerText = value;
				found = true;
			}

			if (found)
				return;

			// Not all projects have a MtouchExtraArgs node, so create one of none was found.
			var propertyGroups = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup' and @Condition]");
			foreach (XmlNode pg in propertyGroups) {
				if (!EvaluateCondition (pg, platform, configuration))
					continue;

				var mea = csproj.CreateElement (node, csproj.GetNamespace ());
				mea.InnerText = value;
				pg.AppendChild (mea);
			}
		}

		static string GetNode (this XmlDocument csproj, string name, string platform, string configuration)
		{
			foreach (var pg in GetPropertyGroups (csproj, platform, configuration)) {
				foreach (XmlNode node in pg.ChildNodes)
					if (node.Name == name)
						return node.InnerText;
			}

			return null;
		}

		public static List<string> GetImports (this XmlDocument csproj)
		{
			var imports = csproj.SelectNodes ("/*/*[local-name() = 'Import'][not(@Condition)]");
			var rv = new List<string> ();
			foreach (XmlNode import in imports)
				rv.Add (import.Attributes ["Project"].Value);
			return rv;
		}

		public static string GetImport (this XmlDocument csproj)
		{
			return GetImports (csproj).FirstOrDefault ((v) => v.Replace ('/', '\\').Contains ("$(MSBuildExtensionsPath)\\Xamarin"));
		}

		public delegate bool FixReferenceDelegate (string include, string subdir, string suffix, out string fixed_include);

		public static void FixProjectReferences (this XmlDocument csproj, string suffix, FixReferenceDelegate fixCallback)
		{
			FixProjectReferences (csproj, null, suffix, fixCallback);
		}

		public static void FixProjectReferences (this XmlDocument csproj, string subdir, string suffix, FixReferenceDelegate fixCallback)
		{
			var nodes = csproj.SelectNodes ("/*/*/*[local-name() = 'ProjectReference']");
			foreach (XmlNode n in nodes) {
				var nameNode = n ["Name"];
				var includeAttribute = n.Attributes ["Include"];
				var include = includeAttribute.Value;

				include = include.Replace ('\\', '/');
				if (!fixCallback (include, subdir, suffix, out var fixed_include))
					continue;
				var name = Path.GetFileNameWithoutExtension (fixed_include);
				fixed_include = fixed_include.Replace ('/', '\\');

				includeAttribute.Value = fixed_include;
				if (nameNode != null)
					nameNode.InnerText = name;
			}
		}

		public static void FixTestLibrariesReferences (this XmlDocument csproj, string platform)
		{
			var nodes = csproj.SelectNodes ("//*[local-name() = 'ObjcBindingNativeLibrary' or local-name() = 'ObjcBindingNativeFramework']");
			var test_libraries = new string [] {
				"libtest.a",
				"libtest2.a",
				"XTest.framework",
				"XStaticArTest.framework",
				"XStaticObjectTest.framework"
			};
			foreach (XmlNode node in nodes) {
				var includeAttribute = node.Attributes ["Include"];
				if (includeAttribute != null) {
					foreach (var tl in test_libraries)
						includeAttribute.Value = includeAttribute.Value.Replace ($"test-libraries\\.libs\\ios-fat\\{tl}", $"test-libraries\\.libs\\{platform}-fat\\{tl}");
				}
			}
			nodes = csproj.SelectNodes ("//*[local-name() = 'Target' and @Name = 'BeforeBuild']");
			foreach (XmlNode node in nodes) {
				var outputsAttribute = node.Attributes ["Outputs"];
				if (outputsAttribute != null) {
					foreach (var tl in test_libraries)
						outputsAttribute.Value = outputsAttribute.Value.Replace ($"test-libraries\\.libs\\ios-fat\\${tl}", $"test-libraries\\.libs\\{platform}-fat\\${tl}");
				}
			}
		}

		public static void FixArchitectures (this XmlDocument csproj, string simulator_arch, string device_arch, string platform = null, string configuration = null)
		{
			var nodes = csproj.SelectNodes ("/*/*/*[local-name() = 'MtouchArch']");
			if (nodes.Count == 0)
				throw new Exception (string.Format ("Could not find MtouchArch at all"));
			foreach (XmlNode n in nodes) {
				if (platform != null && configuration != null && !IsNodeApplicable (n, platform, configuration))
					continue;
				switch (n.InnerText.ToLower ()) {
				case "i386":
				case "x86_64":
				case "i386, x86_64":
					n.InnerText = simulator_arch;
					break;
				case "armv7":
				case "armv7s":
				case "arm64":
				case "arm64_32":
				case "armv7k":
				case "armv7, arm64":
				case "armv7k, arm64_32":
					n.InnerText = device_arch;
					break;
				default:
					throw new NotImplementedException (string.Format ("Unhandled architecture: {0}", n.InnerText));

				}
			}
		}

		public static void FindAndReplace (this XmlDocument csproj, string find, string replace)
		{
			FindAndReplace (csproj.ChildNodes, find, replace);
		}

		static void FindAndReplace (XmlNode node, string find, string replace)
		{
			if (node.HasChildNodes) {
				FindAndReplace (node.ChildNodes, find, replace);
			} else {
				if (node.NodeType == XmlNodeType.Text)
					node.InnerText = node.InnerText.Replace (find, replace);
			}
			if (node.Attributes != null) {
				foreach (XmlAttribute attrib in node.Attributes)
					attrib.Value = attrib.Value.Replace (find, replace);
			}
		}

		static void FindAndReplace (XmlNodeList nodes, string find, string replace)
		{
			foreach (XmlNode node in nodes)
				FindAndReplace (node, find, replace);
		}

		public static void FixInfoPListInclude (this XmlDocument csproj, string suffix, string fullPath = null, string newName = null)
		{
			var import = GetInfoPListNode (csproj, false);
			if (import != null) {
				var attrib = import.Attributes ["Include"];
				var value = attrib.Value;
				var unixValue = value.Replace ('\\', '/');

				// If newName is specified, use that as-is
				// If not:
				//     If the existing value has a directory, use that as the directory
				//     Otherwise, if fullPath is passed, use that as the directory
				//     Finally, combine the expected Info.plist name with the directory (if there is a directory; there might not be one)
				if (newName == null) {
					var directory = Path.GetDirectoryName (unixValue);
					if (string.IsNullOrEmpty (directory))
						directory = fullPath;

					newName = $"Info{suffix}.plist";
					if (!string.IsNullOrEmpty (directory))
						newName = Path.Combine (directory, newName);
				}
				attrib.Value = newName.Replace ('/', '\\');
				var logicalName = import.SelectSingleNode ("./*[local-name() = 'LogicalName']");
				if (logicalName == null) {
					logicalName = csproj.CreateElement ("LogicalName", csproj.GetNamespace ());
					import.AppendChild (logicalName);
				}
				logicalName.InnerText = "Info.plist";
			}
		}

		public static string GetNamespace (this XmlDocument csproj)
		{
			return IsDotNetProject (csproj) ? null : MSBuild_Namespace;
		}

		public static bool IsDotNetProject (this XmlDocument csproj)
		{
			var project = csproj?.SelectSingleNode ("./*[local-name() = 'Project']");
			var attrib = project?.Attributes ["Sdk"];
			return attrib != null;
		}

		public static bool? GetEnableDefaultItems (this XmlDocument csproj)
		{
			var node = csproj.SelectSingleNode ($"/*/*/*[local-name() = 'EnableDefaultItems']");
			if (node == null)
				return null;
			return string.Equals (node.InnerText, "true", StringComparison.OrdinalIgnoreCase);
		}

		public static XmlNode GetInfoPListNode (this XmlDocument csproj, bool throw_if_not_found = false)
		{
			var noLogicalName = csproj.SelectSingleNode ("//*[(local-name() = 'None' or local-name() = 'BundleResource' or local-name() = 'Content') and @Include = 'Info.plist']");
			if (noLogicalName != null)
				return noLogicalName;

			var logicalName = csproj.SelectSingleNode ("//*[(local-name() = 'None' or local-name() = 'Content' or local-name() = 'BundleResource')]/*[local-name()='LogicalName' and text() = 'Info.plist']");
			if (logicalName != null)
				return logicalName.ParentNode;

			if (throw_if_not_found)
				throw new Exception ($"Could not find Info.plist include.");
			return null;
		}

		public static string GetInfoPListInclude (this XmlDocument csproj)
		{
			return GetInfoPListNode (csproj)?.Attributes ["Include"]?.Value;
		}

		public static IEnumerable<string> GetProjectReferences (this XmlDocument csproj)
		{
			var nodes = csproj.SelectNodes ("//*[local-name() = 'ProjectReference']");
			foreach (XmlNode node in nodes)
				yield return node.Attributes ["Include"].Value;
		}

		public static IEnumerable<string> GetExtensionProjectReferences (this XmlDocument csproj)
		{
			var nodes = csproj.SelectNodes ("//*[local-name() = 'ProjectReference']");
			foreach (XmlNode node in nodes) {
				if (node.SelectSingleNode ("./*[local-name () = 'IsAppExtension']") != null)
					yield return node.Attributes ["Include"].Value;
			}
		}

		public static IEnumerable<string> GetNunitAndXunitTestReferences (this XmlDocument csproj)
		{
			var nodes = csproj.SelectNodes ("//*[local-name() = 'Reference']");
			foreach (XmlNode node in nodes) {
				var includeValue = node.Attributes ["Include"].Value;
				if (includeValue.EndsWith ("_test.dll", StringComparison.Ordinal) || includeValue.EndsWith ("_xunit-test.dll", StringComparison.Ordinal))
					yield return includeValue;
			}
		}

		public static void SetSdk (this XmlDocument csproj, string sdk)
		{
			var node = csproj.SelectSingleNode ("//*[local-name() = 'Project']");
			if (node == null)
				throw new Exception ($"Could not find a 'Project' node");
			var attrib = node.Attributes ["Sdk"];
			if (attrib == null)
				throw new Exception ($"The 'Project' node doesn't have an 'Sdk' attribute");
			attrib.Value = sdk;
		}

		public static void SetRuntimeIdentifier (this XmlDocument csproj, string runtimeIdentifier)
		{
			var node = csproj.SelectSingleNode ("//*[local-name() = 'RuntimeIdentifier']");
			if (node == null)
				throw new Exception ($"Could not find a 'RuntimeIdentifier' node");
			node.InnerText = runtimeIdentifier;
		}

		public static void SetProjectReferenceValue (this XmlDocument csproj, string projectInclude, string node, string value)
		{
			var nameNode = csproj.SelectSingleNode ("//*[local-name() = 'ProjectReference' and @Include = '" + projectInclude + "']/*[local-name() = '" + node + "']");
			nameNode.InnerText = value;
		}

		public static string GetAssetTargetFallback (this XmlDocument csproj)
		{
			return csproj.SelectSingleNode ("//*[local-name() = 'AssetTargetFallback']")?.InnerText;
		}

		public static void SetAssetTargetFallback (this XmlDocument csproj, string value)
		{
			var node = csproj.SelectSingleNode ("//*[local-name() = 'AssetTargetFallback']");
			if (node != null)
				node.InnerText = value;
		}

		public static void SetProjectReferenceInclude (this XmlDocument csproj, string projectInclude, string value)
		{
			var elements = csproj.SelectElementNodes ("ProjectReference");
			elements
				  .Where ((v) =>
					{
						var attrib = v.Attributes ["Include"];
						if (attrib == null)
							return false;
						return attrib.Value == projectInclude;
					})
				  .Single ()
				  .Attributes ["Include"].Value = value;
		}

		public static void CreateProjectReferenceValue (this XmlDocument csproj, string existingInclude, string path, string guid, string name)
		{
			var referenceNode = csproj.SelectSingleNode ("//*[local-name() = 'Reference' and @Include = '" + existingInclude + "']");
			var projectReferenceNode = csproj.CreateElement ("ProjectReference", csproj.GetNamespace ());
			var includeAttribute = csproj.CreateAttribute ("Include");
			includeAttribute.Value = path.Replace ('/', '\\');
			projectReferenceNode.Attributes.Append (includeAttribute);
			var projectNode = csproj.CreateElement ("Project", csproj.GetNamespace ());
			projectNode.InnerText = guid;
			projectReferenceNode.AppendChild (projectNode);
			var nameNode = csproj.CreateElement ("Name", csproj.GetNamespace ());
			nameNode.InnerText = name;
			projectReferenceNode.AppendChild (nameNode);

			XmlNode itemGroup;
			if (referenceNode != null) {
				itemGroup = referenceNode.ParentNode;
				referenceNode.ParentNode.RemoveChild (referenceNode);
			} else {
				itemGroup = csproj.CreateElement ("ItemGroup", csproj.GetNamespace ());
				csproj.SelectSingleNode ("//*[local-name() = 'Project']").AppendChild (itemGroup);
			}
			itemGroup.AppendChild (projectReferenceNode);
		}

		static XmlNode CreateItemGroup (this XmlDocument csproj)
		{
			var lastItemGroup = csproj.SelectSingleNode ("//*[local-name() = 'ItemGroup'][last()]");
			var newItemGroup = csproj.CreateElement ("ItemGroup", csproj.GetNamespace ());
			lastItemGroup.ParentNode.InsertAfter (newItemGroup, lastItemGroup);
			return newItemGroup;
		}

		public static void AddAdditionalDefines (this XmlDocument csproj, string value)
		{
			var mainPropertyGroup = csproj.SelectSingleNode ("//*[local-name() = 'PropertyGroup' and not(@Condition)]");
			var mainDefine = mainPropertyGroup.SelectSingleNode ("*[local-name() = 'DefineConstants']");
			if (mainDefine == null) {
				mainDefine = csproj.CreateElement ("DefineConstants", csproj.GetNamespace ());
				mainDefine.InnerText = value;
				mainPropertyGroup.AppendChild (mainDefine);
			} else {
				mainDefine.InnerText = mainDefine.InnerText + ";" + value;
			}

			// make sure all other DefineConstants include the main one
			var otherDefines = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup' and @Condition]/*[local-name() = 'DefineConstants']");
			foreach (XmlNode def in otherDefines) {
				if (!def.InnerText.Contains ("$(DefineConstants"))
					def.InnerText = def.InnerText + ";$(DefineConstants)";
			}
		}

		public static void RemoveDefines (this XmlDocument csproj, string defines, string platform = null, string configuration = null)
		{
			var separator = new char [] { ';' };
			var defs = defines.Split (separator, StringSplitOptions.RemoveEmptyEntries);
			var projnode = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup']/*[local-name() = 'DefineConstants']");
			foreach (XmlNode xmlnode in projnode) {
				if (string.IsNullOrEmpty (xmlnode.InnerText))
					continue;

				var parent = xmlnode.ParentNode;
				if (!IsNodeApplicable (parent, platform, configuration))
					continue;

				var existing = xmlnode.InnerText.Split (separator, StringSplitOptions.RemoveEmptyEntries);
				var any = false;
				foreach (var def in defs) {
					for (var i = 0; i < existing.Length; i++) {
						if (existing [i] == def) {
							existing [i] = null;
							any = true;
						}
					}
				}
				if (!any)
					continue;
				xmlnode.InnerText = string.Join (separator [0].ToString (), existing.Where ((v) => !string.IsNullOrEmpty (v)));
			}
		}

		public static void AddAdditionalDefines (this XmlDocument csproj, string value, string platform, string configuration)
		{
			var projnode = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup' and @Condition]/*[local-name() = 'DefineConstants']");
			foreach (XmlNode xmlnode in projnode) {
				var parent = xmlnode.ParentNode;
				if (parent.Attributes ["Condition"] == null)
					continue;
				if (!IsNodeApplicable (parent, platform, configuration))
					continue;
				
				if (string.IsNullOrEmpty (xmlnode.InnerText)) {
					xmlnode.InnerText = value;
				} else {
					xmlnode.InnerText += ";" + value;
				}
				return;
			}

			projnode = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup' and @Condition]");
			foreach (XmlNode xmlnode in projnode) {
				if (xmlnode.Attributes ["Condition"] == null)
					continue;
				if (!IsNodeApplicable (xmlnode, platform, configuration))
					continue;

				var defines = csproj.CreateElement ("DefineConstants", csproj.GetNamespace ());
				defines.InnerText = "$(DefineConstants);" + value;
				xmlnode.AppendChild (defines);
				return;
			}

			var newPropertyGroup = csproj.AddPropertyGroup (platform, configuration);
			var defineConstantsElement = csproj.CreateElement ("DefineConstants", csproj.GetNamespace ());
			defineConstantsElement.InnerText = "$(DefineConstants);" + value;
			newPropertyGroup.AppendChild (defineConstantsElement);
		}

		static XmlNode AddPropertyGroup (this XmlDocument csproj, string platform, string configuration)
		{
			// Create a new PropertyGroup with the desired condition, and add it just after the last PropertyGroup in the csproj.
			var projectNode = csproj.SelectSingleNode ("//*[local-name() = 'Project']");
			var lastPropertyGroup = csproj.SelectNodes ("/*[local-name() = 'Project']/*[local-name() = 'PropertyGroup']").Cast<XmlNode> ().Last ();
			var newPropertyGroup = csproj.CreateElement ("PropertyGroup", csproj.GetNamespace ());
			if (!string.IsNullOrEmpty (platform) || !string.IsNullOrEmpty (configuration)) {
				// Condition=" '$(Configuration)|$(Platform)' == 'Debug|iPhoneSimulator' "
				var conditionAttribute = csproj.CreateAttribute ("Condition");
				var left = string.Empty;
				var right = string.Empty;
				if (!string.IsNullOrEmpty (configuration)) {
					left = "$(Configuration)";
					right = configuration;
				}
				if (!string.IsNullOrEmpty (platform)) {
					if (!string.IsNullOrEmpty (left)) {
						left += "|";
						right += "|";
					}
					left += "$(Platform)";
					right += platform;
				}
				conditionAttribute.Value = $"'{left}' == '{right}'";
				newPropertyGroup.Attributes.Append (conditionAttribute);
			}
			projectNode.InsertAfter (newPropertyGroup, lastPropertyGroup);
			return newPropertyGroup;
		}

		public static void AddTopLevelProperty (this XmlDocument csproj, string property, string value)
		{
			var propertyGroup = csproj.SelectNodes ("//*[local-name() = 'PropertyGroup' and not(@Condition)]") [0];

			var propertyNode = csproj.CreateElement (property, csproj.GetNamespace ());
			propertyNode.InnerText = value;
			propertyGroup.AppendChild (propertyNode);
		}

		public static void SetNode (this XmlDocument csproj, string node, string value)
		{
			var nodes = csproj.SelectNodes ("/*/*/*[local-name() = '" + node + "']");
			if (nodes.Count == 0)
				throw new Exception (string.Format ("Could not find node {0}", node));
			foreach (XmlNode n in nodes) {
				n.InnerText = value;
			}
		}

		public static void RemoveNode (this XmlDocument csproj, string node, bool throwOnInexistentNode = true)
		{
			var nodes = csproj.SelectNodes ("/*/*/*[local-name() = '" + node + "']");
			if (throwOnInexistentNode && nodes.Count == 0)
				throw new Exception (string.Format ("Could not find node {0}", node));
			foreach (XmlNode n in nodes) {
				n.ParentNode.RemoveChild (n);
			}
		}

		public static void CloneConfiguration (this XmlDocument csproj, string platform, string configuration, string new_configuration)
		{
			var projnode = csproj.GetPropertyGroups (platform, configuration);
			foreach (XmlNode xmlnode in projnode) {
				var clone = xmlnode.Clone ();
				var condition = clone.Attributes ["Condition"];
				condition.InnerText = condition.InnerText.Replace (configuration, new_configuration);
				xmlnode.ParentNode.InsertAfter (clone, xmlnode);
				return;
			}

			throw new Exception ($"Configuration {platform}|{configuration} not found.");
		}

		public static void DeleteConfiguration (this XmlDocument csproj, string platform, string configuration)
		{
			var projnode = csproj.GetPropertyGroups (platform, configuration);
			foreach (XmlNode xmlnode in projnode)
				xmlnode.ParentNode.RemoveChild (xmlnode);
		}

		static IEnumerable<XmlNode> SelectElementNodes (this XmlNode node, string name)
		{
			foreach (XmlNode child in node.ChildNodes) {
				if (child.NodeType == XmlNodeType.Element && child.Name == name)
					yield return child;

				if (!child.HasChildNodes)
					continue;
				
				foreach (XmlNode descendent in child.SelectElementNodes (name))
					yield return descendent;
			}
		}

		public static void ResolveAllPaths (this XmlDocument csproj, string project_path, string rootDirectory = null)
		{
			var dir = System.IO.Path.GetDirectoryName (project_path);
			var nodes_with_paths = new string []
			{
				"AssemblyOriginatorKeyFile",
				"CodesignEntitlements",
				"TestLibrariesDirectory",
				"HintPath",
				"RootTestsDirectory",
			};
			var attributes_with_paths = new string [] []
			{
				new string [] { "None", "Include" },
				new string [] { "Compile", "Include" },
				new string [] { "Compile", "Exclude" },
				new string [] { "ProjectReference", "Include" },
				new string [] { "InterfaceDefinition", "Include" },
				new string [] { "BundleResource", "Include" },
				new string [] { "EmbeddedResource", "Include" },
				new string [] { "ImageAsset", "Include" },
				new string [] { "GeneratedTestInput", "Include" },
				new string [] { "GeneratedTestOutput", "Include" },
				new string [] { "TestLibrariesInput", "Include" },
				new string [] { "TestLibrariesOutput", "Include" },
				new string [] { "Content", "Include" },
				new string [] { "ObjcBindingApiDefinition", "Include" },
				new string [] { "ObjcBindingCoreSource", "Include" },
				new string [] { "ObjcBindingNativeLibrary", "Include" },
				new string [] { "ObjcBindingNativeFramework", "Include" },
				new string [] { "Import", "Project", "CustomBuildActions.targets", "..\\shared.targets" },
				new string [] { "FilesToCopy", "Include" },
				new string [] { "FilesToCopyFoo", "Include" },
				new string [] { "FilesToCopyFooBar", "Include" },
				new string [] { "FilesToCopyEncryptedXml", "Include" },
				new string [] { "FilesToCopyCryptographyPkcs", "Include" },
				new string [] { "FilesToCopyResources", "Include" },
				new string [] { "FilesToCopyXMLFiles", "Include" },
				new string [] { "FilesToCopyChannels", "Include" },
				new string [] { "CustomMetalSmeltingInput", "Include" },
				new string [] { "Metal", "Include" },
			};
			var nodes_with_variables = new string []
			{
				"MtouchExtraArgs",
			};
			Func<string, string> convert = null;
			convert = (input) =>
			{
				if (input.IndexOf (';') >= 0) {
					var split = input.Split (new char [] { ';' }, StringSplitOptions.RemoveEmptyEntries);
					for (var i = 0; i < split.Length; i++)
						split [i] = convert (split [i]);
					return string.Join (";", split);
				}

				if (input [0] == '/')
					return input; // This is already a full path.

				input = input.Replace ('\\', '/'); // make unix-style

				if (rootDirectory != null)
					input = input.Replace ("$(RootTestsDirectory)", rootDirectory);

				// Don't process anything that starts with a variable, it's either a full path already, or the variable will be updated according to the new location
				if (input.StartsWith ("$(", StringComparison.Ordinal))
					return input;

				input = System.IO.Path.GetFullPath (System.IO.Path.Combine (dir, input));
				input = input.Replace ('/', '\\'); // make windows-style again
				return input;
			};

			foreach (var key in nodes_with_paths) {

				var nodes = csproj.SelectElementNodes (key);
				foreach (var node in nodes)
					node.InnerText = convert (node.InnerText);
			}
			foreach (var key in nodes_with_variables) {
				var nodes = csproj.SelectElementNodes (key);
				foreach (var node in nodes) {
					node.InnerText = node.InnerText.Replace ("${ProjectDir}", StringUtils.Quote (System.IO.Path.GetDirectoryName (project_path)));
				}
			}
			foreach (var kvp in attributes_with_paths) {
				var element = kvp [0];
				var attrib = kvp [1];
				var nodes = csproj.SelectElementNodes (element);
				foreach (XmlNode node in nodes) {
					var a = node.Attributes [attrib];
					if (a == null)
						continue;

					// entries after index 2 is a list of values to filter the attribute value against.
					var found = kvp.Length == 2;
					var skipLogicalName = kvp.Length > 2;
					for (var i = 2; i < kvp.Length; i++)
						found |= a.Value == kvp [i];
					if (!found)
						continue;
					
					// Fix any default LogicalName values (but don't change existing ones).
					var ln = node.SelectElementNodes ("LogicalName")?.SingleOrDefault ();
					var links = node.SelectElementNodes ("Link");
					if (!skipLogicalName && ln == null && !links.Any ()) {
						ln = csproj.CreateElement ("LogicalName", csproj.GetNamespace ());
						node.AppendChild (ln);

						string logicalName = a.Value;
						switch (element) {
						case "BundleResource":
							if (logicalName.StartsWith ("Resources\\", StringComparison.Ordinal))
								logicalName = logicalName.Substring ("Resources\\".Length);
							break;
						default:
							break;
						}
						ln.InnerText = logicalName;
					}

					a.Value = convert (a.Value);
				}
			}
		}

		// Retrieves a property from an MSBuild project file by executing MSBuild and getting MSBuild to print the property.
		// We do this by creating a custom MSBuild file which:
		// * Will import the project file we're inspecting
		// * Has a target that will print a given property
		// and then executing MSBuild on this custom MSBuild file.
		public static async Task<string> GetPropertyByMSBuildEvaluationAsync (this XmlDocument csproj, ILog log, IProcessManager processManager, string projectPath, string evaluateProperty, string dependsOnTargets = "", Dictionary<string, string> properties = null)
		{
			var xml =
@"<Project DefaultTargets='WriteProperty' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>
	<!-- Import the project we want to inspect -->
	<Import Project='$(ProjectFile)' Condition=""'$(ProjectFile)' != ''"" />
	<!-- Target to write out the property we want -->
	<Target Name='WriteProperty' DependsOnTargets='%DEPENDSONTARGETS%'>
		<PropertyGroup>
			<_Properties>$(%PROPERTY%)</_Properties>
		</PropertyGroup>
		<Error Text='The ProjectFile variable must be set.' Condition=""'$(ProjectFile)' == ''"" />
		<Error Text='The OutputFile variable must be set.' Condition=""'$(OutputFile)' == ''"" />
		<WriteLinesToFile File='$(OutputFile)' Lines='$(_Properties)' Overwrite='true' />
	</Target>
</Project>
";

			var dir = Path.GetDirectoryName (projectPath);
			var inspector = Path.Combine (dir, "PropertyInspector.csproj");
			var output = Path.Combine (dir, "PropertyInspector.txt");
			try {
				File.WriteAllText (inspector, xml.Replace ("%PROPERTY%", evaluateProperty).Replace ("%DEPENDSONTARGETS%", dependsOnTargets));
				using (var proc = new Process ()) {
					var isDotNetProject = csproj.IsDotNetProject ();
					proc.StartInfo.FileName = isDotNetProject ? processManager.GetDotNetExecutable (projectPath) : processManager.MSBuildPath;
					var args = new List<string> ();
					if (isDotNetProject)
						args.Add ("build");
					args.Add ("/p:ProjectFile=" + projectPath);
					args.Add ("/p:OutputFile=" + output);
					foreach (var prop in properties)
						args.Add ($"/p:{prop.Key}={prop.Value}");
					args.Add (inspector);
					var env = new Dictionary<string, string> {
						{ "MSBUILD_EXE_PATH", null },
					};
					proc.StartInfo.Arguments = StringUtils.FormatArguments (args);
					proc.StartInfo.WorkingDirectory = dir;
					var rv = await processManager.RunAsync (proc, log, environment_variables: env, timeout: TimeSpan.FromSeconds (15));
					if (!rv.Succeeded)
						throw new Exception ($"Unable to evaluate the property {evaluateProperty}.");
					return File.ReadAllText (output).Trim ();
				}
			} finally {
				File.Delete (inspector);
				File.Delete (output);
			}
		}
	}
}
