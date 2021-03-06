using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

#if NET472
namespace System.Diagnostics.CodeAnalysis {
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]

    public class NotNullWhenAttribute : Attribute {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;
        public bool ReturnValue { get; }
    }
}
#endif

namespace JsonToItemsTaskFactory
{

    /// <summary>Reads a json input blob and populates some output items</summary>
    ///
    /// <example>JSON should follow this structure - the toplevel "properties" and "items" keys are exact, other keys are arbitrary.
    /// <code>
    /// {
    ///    "properties" : {
    ///      "propName1": "value1",
    ///      "propName2": "value"
    ///    },
    ///    "items" : {
    ///      "itemName1": [ "stringValue", { "identity": "anotherValue", "metadataKey": "metadataValue", ... }, "thirdValue" ],
    ///      "itemName2": [ ... ]
    /// }
    /// </code>
    ///
    /// A task can be declared by
    ///
    /// <code>
    /// <UsingTask AssemblyFile="..." TaskName="MyJsonReader" TaskFactory="Microsoft.DotNet.Runtime.Tasks.JsonToItemsTaskFactory">
    ///   <ParameterGroup>
    ///     <PropName1 ParameterType="System.String" Required="False" Output="True" />
    ///     <ItemName1 ParameterType="Microsoft.Build.Framework.ITaskItem[]" Required="False" Output="True" />
    ///   <ParameterGroup>
    /// </UsingTask>
    /// </code> 
    ///
    /// And then used in a target.  The `JsonFilePath' attribute is used to specify the json file to read.
    ///
    /// <code>
    /// <Target Name="UseMyReader">
    ///   <MyJsonReader JsonFilePath="foo.json">
    ///     <Output TaskParameter="PropName1" PropertyName="MyParsedProperty" />
    ///     <Output TaskParameter="ItemName1" ItemName="MyParsedItems" />
    ///   </MyJsonReader>
    ///   <Message Priority="High" Text=" Got property $(MyParsedProperty) and items @(MyParsedItems)" />
    /// </Target>
    /// </code>
    /// </example>
    public class JsonToItemsTaskFactory : ITaskFactory
    {
        const string JsonFilePath = "JsonFilePath";
        private TaskPropertyInfo[]? _taskProperties;
        private string? _taskName;

        private bool logDebugTask;

        public JsonToItemsTaskFactory() {}

        public string FactoryName => "JsonToItemsTaskFactory";

        public Type TaskType => typeof(JsonToItemsTask);

        public bool Initialize (string taskName, IDictionary<string,TaskPropertyInfo> parameterGroup, string? taskBody, IBuildEngine taskFactoryLoggingHost) {
            _taskName = taskName;
            if (taskBody != null && taskBody.StartsWith("debug", StringComparison.InvariantCultureIgnoreCase))
                logDebugTask = true;
            var log = new TaskLoggingHelper(taskFactoryLoggingHost, _taskName);
            if (!ValidateParameterGroup (parameterGroup, log))
                return false;
            _taskProperties = new TaskPropertyInfo[parameterGroup.Count + 1];
            _taskProperties[0] = new TaskPropertyInfo(nameof(JsonFilePath), typeof(string), output: false, required: true);
            parameterGroup.Values.CopyTo(_taskProperties, 1);
            return true;
        }

        public TaskPropertyInfo[] GetTaskParameters () => _taskProperties!;

        public ITask CreateTask (IBuildEngine taskFactoryLoggingHost)
        {
            var log = new TaskLoggingHelper(taskFactoryLoggingHost, _taskName);
            if (logDebugTask) log.LogMessage("CreateTask called");
            return new JsonToItemsTask(_taskName!, logDebugTask);
        }

        public void CleanupTask (ITask task) {}

        internal bool ValidateParameterGroup (IDictionary<string, TaskPropertyInfo> parameterGroup, TaskLoggingHelper log)
        {
            bool hasErrors = false;
            var taskName = _taskName ?? "";
            foreach (var kvp in parameterGroup) {
                var propName = kvp.Key;
                var propInfo = kvp.Value;
                if (String.Equals(propName, nameof(JsonFilePath), StringComparison.InvariantCultureIgnoreCase)) {
                    log.LogError($"Task {taskName}: {nameof(JsonFilePath)} parameter must not be declared. It is implicitly added by the task.");
                    hasErrors = true;
                    continue;
                }

                if (!propInfo.Output) {
                    log.LogError($"Task {taskName}: parameter {propName} is not an output. All parameters except {nameof(JsonFilePath)} must be outputs");
                    hasErrors = true;
                    continue;
                }
                if (propInfo.Required) {
                    log.LogError($"Task {taskName}: parameter {propName} is an output but is marked required. That's not supported.");
                    hasErrors = true;
                }
                if (typeof(ITaskItem[]).IsAssignableFrom(propInfo.PropertyType))
                    continue; // ok, an item list
                if (typeof(string).IsAssignableFrom(propInfo.PropertyType))
                    continue; // ok, a string property

                log.LogError($"Task {taskName}: parameter {propName} is not an output of type System.String or Microsoft.Build.Framework.ITaskItem[]");
                hasErrors = true;
            }
            return !hasErrors;
        }

        public class JsonToItemsTask : IGeneratedTask {
            

            IBuildEngine? _buildEngine;
            public IBuildEngine BuildEngine { get => _buildEngine!; set { _buildEngine = value; SetBuildEngine(value);} }
            public ITaskHost? HostObject { get; set; }

            TaskLoggingHelper? _log;
            TaskLoggingHelper Log { get => _log!; set { _log = value; } }

            private void SetBuildEngine (IBuildEngine buildEngine)
            {
                Log = new TaskLoggingHelper(buildEngine, TaskName);
            }

            public static JsonSerializerOptions JsonOptions => new () {
                            PropertyNameCaseInsensitive =  true,
                            AllowTrailingCommas = true,
                        };
            private string? jsonFilePath;

            private readonly bool logDebugTask; // print stuff to the log for debugging the task

            private JsonModelRoot? jsonModel;
            public string TaskName {get;}
            public JsonToItemsTask(string taskName, bool logDebugTask = false) {
                TaskName = taskName;
                this.logDebugTask = logDebugTask;
            }

            public bool Execute() {
                if (jsonFilePath == null) {
                    Log.LogError("no JsonFilePath specified");
                    return false;
                }
                if (!TryGetJson (jsonFilePath, out var json))
                    return false;

                if (logDebugTask) {
                    LogParsedJson (json);
                }
                jsonModel = json;
                return true;
            }

            public bool TryGetJson(string jsonFilePath, [NotNullWhen(true)] out JsonModelRoot? json) {
                FileStream? file = null;
                try {
                    try {
                        file = File.OpenRead(jsonFilePath);
                    } catch (FileNotFoundException fnfe) {
                        Log.LogErrorFromException(fnfe);
                        json = null;
                        return false;
                    }        
                    json = JsonSerializer.DeserializeAsync<JsonModelRoot>(file, JsonOptions).AsTask().Result;
                    if (json == null) {
                        Log.LogError ("Failed to deserialize Json file");
                        return false;
                    }
                    return true;
                } finally {
                    if (file != null) {
                        file.Dispose();
                    }
                }
            }

            internal void LogParsedJson (JsonModelRoot json) {
                if (json.Properties != null) {
                    Log.LogMessage ("json has properties: ");
                    foreach (var property in json.Properties) {
                        Log.LogMessage($"  {property.Key} = {property.Value}");
                    }
                }
                if (json.Items != null) {
                    Log.LogMessage("items: ");
                    foreach (var item in json.Items) {
                        Log.LogMessage($"  {item.Key} = [");
                        foreach (var value in item.Value) {
                            Log.LogMessage($"    {value.Identity}");
                            if (value.Metadata != null) {
                                Log.LogMessage("       and some metadata, too");
                            }
                        }
                        Log.LogMessage("  ]");                               
                    }
                }
            }

            public object? GetPropertyValue (TaskPropertyInfo property) {
                bool isItem = false;
                if (typeof (ITaskItem[]).IsAssignableFrom (property.PropertyType)) {
                    if (logDebugTask) Log.LogMessage("GetPropertyValue called with @({0})", property.Name);
                    isItem = true;
                } else {
                    if (logDebugTask) Log.LogMessage("GetPropertyValue called with $({0})", property.Name);
                }
                if (!isItem) {
                    if (jsonModel?.Properties != null &&  jsonModel.Properties.TryGetValue(property.Name, out var value)) {
                        return value;
                    }
                    Log.LogError("Property {0} not found in {1}", property.Name, jsonFilePath);
                    throw new Exception();
                } else {
                    if (jsonModel?.Items != null && jsonModel.Items.TryGetValue(property.Name, out var itemModels)) {
                        return ConvertItems (itemModels);
                    }

                }
                return null;
            }

            public static ITaskItem[] ConvertItems(JsonModelItem[] itemModels) {
                var items = new ITaskItem[itemModels.Length];
                for (int i = 0; i < itemModels.Length; i++) {
                    var itemModel = itemModels[i];
                    var item = new TaskItem(itemModel.Identity);
                    if (itemModel.Metadata != null) {
                        foreach (var metadata in itemModel.Metadata) {
                            if (string.Equals(metadata.Key, "Identity", StringComparison.OrdinalIgnoreCase))
                                continue;
                            item.SetMetadata(metadata.Key, metadata.Value);
                        }
                    }
                    items[i] = item;
                }
                return items;
            }

            public void SetPropertyValue (TaskPropertyInfo property, object? value) {
                if (logDebugTask) Log.LogMessage("SetPropertyValue called with {0}", property.Name);
                if (property.Name == "JsonFilePath") {
                    jsonFilePath = (string)value!;
                } else
                    throw new Exception ($"JsonToItemsTask {TaskName} cannot set property {property.Name}");
            }

        }

        public class JsonModelRoot {
            [JsonConverter(typeof(CaseInsensitiveDictionaryConverter))]
            public Dictionary<string,string>?  Properties {get; set;}
            public Dictionary<string,JsonModelItem[]>? Items {get; set;}

            public JsonModelRoot() {}
        }

        [JsonConverter(typeof(JsonModelItemConverter))]
        public class JsonModelItem {
            public string? Identity {get; set;}
            // n.b. will  be deserialized case insensitive
            public Dictionary<string,string>? Metadata {get;  set; }
        }

        public class CaseInsensitiveDictionaryConverter : JsonConverter<Dictionary<string,string>> {
            public override Dictionary<string,string>? Read (ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
                var dict = JsonSerializer.Deserialize<Dictionary<string,string>>(ref reader, options);
                if (dict == null)
                    return null;
                return new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
            }
            public override void Write (Utf8JsonWriter writer, Dictionary<string,string>? value, JsonSerializerOptions options) =>
                JsonSerializer.Serialize(writer, value, options);
        }
        public  class JsonModelItemConverter : JsonConverter<JsonModelItem> {
            public JsonModelItemConverter() {}

            public override JsonModelItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
                switch (reader.TokenType) {
                    case JsonTokenType.String:
                        return new JsonModelItem { Identity = reader.GetString() };
                    case JsonTokenType.StartObject:
                        var item = new JsonModelItem();
                        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
                        if (dict == null)
                            return null;
                        var idict = new Dictionary<string,string> (dict, StringComparer.OrdinalIgnoreCase);
                        if  (!idict.TryGetValue("Identity", out var identity) || identity == null)
                            throw new Exception ("deserialized json dictionary item did not have an Identity metadata");
                        item.Identity = identity;
                        item.Metadata = idict;
                        return item;
                    default:
                        throw new Exception();
                }
            }
            public override void Write(Utf8JsonWriter writer, JsonModelItem value, JsonSerializerOptions options) {
                if (value.Metadata == null)
                    JsonSerializer.Serialize(writer, value.Identity!);
                else {
                    JsonSerializer.Serialize(writer, value.Metadata); /* assumes Identity is in there */
                }
            }
        }; 

    }

}
