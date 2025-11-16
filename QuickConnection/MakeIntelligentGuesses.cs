using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Neo4j.Driver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Windows;


namespace QuickConnection;

public static class DynamicCypherBuilder
{
    public static string BuildDynamicPattern(List<string> names, List<string> targets, int outDepth)
    {
        // Example: for names = [ "Name_A", "Name_B", "Name_C" ]
        // the generated query will be:
        // MATCH (node0)-[r1]->(node1)-[r2]->(node2)
        // WHERE node0.componentGuid = 'GUID_A' AND node1.componentGuid = 'GUID_B' AND node2.componentGuid = 'GUID_C'
        // WITH node2
        // MATCH (node2)-[rOut]->(next)
        // RETURN next.componentName
        StringBuilder patternBuilder = new StringBuilder();
        patternBuilder.Append("MATCH ");
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0)
                patternBuilder.Append("-[r" + i + " {TargetName:'" + targets[i - 1] + "'}]->");
            patternBuilder.Append($"(node{i})");
        }
        patternBuilder.Append("\nWHERE ");
        for (int i = 0; i < names.Count; i++)
        {
            patternBuilder.Append($"node{i}.ComponentName = '{names[i]}'");
            if (i < names.Count - 1)
                patternBuilder.Append(" AND ");
        }
        patternBuilder.Append($"\nWITH node{names.Count - 1}");

        string matchOut = $"\nMATCH (node{names.Count - 1})";
        string _return = $"\nRETURN DISTINCT node{names.Count - 1}.ComponentGuid";
        for (int i = 0; i < outDepth; i++)
        {
            matchOut += $"-[rOut{i}]->(next{i})";
            _return += $",rOut{i}.SourceName,rOut{i}.TargetName, next{i}.ComponentGuid";
        }
        patternBuilder.Append(matchOut);
        patternBuilder.Append(_return);
        return patternBuilder.ToString();
    }
}

public static class ComponentTraversal
{
    public static void TraverseUpstream(IGH_DocumentObject obj, IDriver driver, HashSet<Guid> visited, List<CreateObjectItem> guesses, List<string> currentChain, List<string> currentTargets, int depth, int outDepth)
    {
        if (visited.Contains(obj.InstanceGuid))
            return;
        visited.Add(obj.InstanceGuid);

        // Case 1: The object is a parameter (IGH_Param) which may have source connections.
        if (obj is IGH_Param param)
        {
            foreach (IGH_Param source in param.Sources)
            {
                IGH_DocumentObject upstreamObj = source.Attributes.GetTopLevel.DocObject;
                List<string> newChain = new List<string>
                {
                    upstreamObj.Name
                };
                newChain.AddRange(currentChain);

                List<string> newTargets = new List<string>
                {
                    param.Name
                };
                newTargets.AddRange(currentTargets);

                if (newChain.Count == depth)
                {
                    // Build and store the dynamic query for the current chain.
                    string query = DynamicCypherBuilder.BuildDynamicPattern(newChain, newTargets, outDepth);

                    // Run the query asynchronously and then block to get the result.
                    var cursor = driver.AsyncSession().RunAsync(query).GetAwaiter().GetResult();

                    // Retrieve all records returned by this query.
                    var records = cursor.ToListAsync().GetAwaiter().GetResult();

                    if (records.Count > 0)
                    {
                        foreach (var record in records)
                        {
                            // Get the component from the GUID to test if it exists
                            Guid expected_guid = new Guid(record["next0.ComponentGuid"].ToString());
                            var _proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(expected_guid);
                            if (_proxy == null) continue;

                            var newGuess = new CreateObjectItem(expected_guid, 0, "", false);
                            newGuess.InputParamName = record["rOut0.TargetName"].ToString();

                            var multiItems = new List<CreateObjectItem>();

                            bool allComponentsExist = true;
                            for (int i = 0; i < depth - 1; i++)
                            {
                                Guid next_expected_guid = new Guid(record[$"next{i}.ComponentGuid"].ToString());

                                var _next_proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(next_expected_guid);
                                if (_next_proxy == null)
                                {
                                    allComponentsExist = false;
                                    break;
                                }

                                var nextGuess = new CreateObjectItem(next_expected_guid, 0, "", false);
                                nextGuess.InputParamName = record[$"rOut{i}.TargetName"].ToString();
                                nextGuess.OutputParamName = record[$"rOut{i}.SourceName"].ToString();
                                multiItems.Add(nextGuess);
                            }

                            if (!allComponentsExist) continue;

                            newGuess.MultiItems = multiItems.ToArray();
                            guesses.Add(newGuess);
                        }
                    }
                }
                else
                {
                    // Continue traversing upstream.
                    TraverseUpstream(upstreamObj, driver, visited, guesses, newChain, newTargets, depth, outDepth);
                }
            }
        }
        // Case 2: The object is a component.
        else if (obj is GH_Component comp)
        {
            foreach (IGH_Param p in comp.Params.Input)
            {
                foreach (IGH_Param source in p.Sources)
                {
                    IGH_DocumentObject upstreamObj = source.Attributes.GetTopLevel.DocObject;
                    List<string> newChain = new List<string>
                    {
                        upstreamObj.Name
                    };
                    newChain.AddRange(currentChain);

                    List<string> newTargets = new List<string>
                    {
                        p.Name
                    };
                    newTargets.AddRange(currentTargets);

                    if (newChain.Count == depth)
                    {
                        string query = DynamicCypherBuilder.BuildDynamicPattern(newChain, newTargets, outDepth);

                        // Run the query asynchronously and then block to get the result.
                        var cursor = driver.AsyncSession().RunAsync(query).GetAwaiter().GetResult();

                        // Retrieve all records returned by this query.
                        var records = cursor.ToListAsync().GetAwaiter().GetResult();

                        if (records.Count > 0)
                        {
                            foreach (var record in records)
                            {
                                // Get the component from the GUID to test if it exists
                                Guid expected_guid = new Guid(record["next0.ComponentGuid"].ToString());
                                var _proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(expected_guid);
                                if (_proxy == null) continue;

                                var newGuess = new CreateObjectItem(expected_guid, 0, "", false);
                                newGuess.InputParamName = record["rOut0.TargetName"].ToString();

                                var multiItems = new List<CreateObjectItem>();

                                bool allComponentsExist = true;
                                for (int i = 0; i < depth - 1; i++)
                                {
                                    Guid next_expected_guid = new Guid(record[$"next{i}.ComponentGuid"].ToString());

                                    var _next_proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(next_expected_guid);
                                    if (_next_proxy == null)
                                    {
                                        allComponentsExist = false;
                                        break;
                                    }

                                    var nextGuess = new CreateObjectItem(next_expected_guid, 0, "", false);
                                    nextGuess.InputParamName = record[$"rOut{i}.TargetName"].ToString();
                                    nextGuess.OutputParamName = record[$"rOut{i}.SourceName"].ToString();
                                    multiItems.Add(nextGuess);
                                }

                                if (!allComponentsExist) continue;

                                newGuess.MultiItems = multiItems.ToArray();
                                guesses.Add(newGuess);
                            }
                        }
                    }
                    else
                    {
                        TraverseUpstream(upstreamObj, driver, visited, guesses, newChain, newTargets, depth, outDepth);
                    }
                }
            }
        }
    }

    public static CreateObjectItem[] GetUpstreamResults(Guid guid, int depth, int outDepth, out List<Guid> visitedGuids)
    {
        IGH_DocumentObject startObj = Grasshopper.Instances.ActiveCanvas?.Document.FindObject(guid, false);
        if (startObj == null)
        {
            visitedGuids = new List<Guid>();
            return null;
        }

        IDriver driver = GraphDatabase.Driver(
            "neo4j+s://916f7f37.databases.neo4j.io",
            AuthTokens.Basic("neo4j", "_GjWi91K3QZkkGg3hA7Itrp-U9dlvzH80JnFsnvvW6I")
        );

        HashSet<Guid> visited = new HashSet<Guid>();
        List<CreateObjectItem> guesses = new List<CreateObjectItem>();
        List<string> initialChain = new List<string> { startObj.Name };
        List<string> initialTarget = new List<string>();
        TraverseUpstream(startObj, driver, visited, guesses, initialChain, initialTarget, depth, outDepth);
        visitedGuids = visited.ToList();
        return guesses.ToArray();
    }
}


public class LLMNamePredictor
{
    public List<string> GenerateBatchText(
    string apiKey,
    List<Guid> expectedGuids,
    List<Guid> visitedGuids)
    {
        var doc = Grasshopper.Instances.ActiveCanvas?.Document;

        var batchGroups = new List<object>();

        foreach (Guid expectedGuid in expectedGuids)
        {
            // Build the per-item list: visited + expected
            List<Guid> newList = new List<Guid>(visitedGuids);
            newList.Add(expectedGuid);

            // Convert GUID list → nodes
            List<Node> nodes = new List<Node>();
            foreach (Guid g in newList)
            {
                var obj = doc?.FindObject(g, false);
                if (obj == null)
                {
                    var proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(g);
                    nodes.Add(new Node
                    {
                        id = proxy?.Guid ?? g,
                        name = proxy?.Desc.Name ?? "Missing",
                        nickname = proxy?.Desc.NickName ?? "",
                        type = proxy?.Desc.Name ?? "Unknown"
                    });
                }
                else
                {
                    nodes.Add(new Node
                    {
                        id = obj.InstanceGuid,
                        name = obj.Name,
                        nickname = obj.NickName,
                        type = obj.GetType().FullName
                    });
                }
            }

            batchGroups.Add(new
            {
                id = expectedGuid.ToString(),
                components = nodes
            });
        }

        // Build JSON for the whole batch
        string batchJson = JsonConvert.SerializeObject(new
        {
            groups = batchGroups
        });

        // Single ChatGPT call
        string response = CallChatGPTBatch(batchJson, apiKey);

        // Parse result: { "names": [ "...", "...", ... ] }
        JObject raw = JObject.Parse(response);

        // STEP 1: get message.content
        string contentJson = raw["choices"]?[0]?["message"]?["content"]?.ToString();

        if (string.IsNullOrWhiteSpace(contentJson))
            throw new Exception("GPT response did not contain message.content");

        // STEP 2: parse content JSON (this contains {"names":[...]})
        JObject content = JObject.Parse(contentJson);

        // STEP 3: extract the names array
        return content["names"].ToObject<List<string>>();
    }

    public class Node
    {
        public Guid id;
        public string name;
        public string nickname;
        public string type;
    }

    public class Edge
    {
        public string from;
        public string to;
        public string from_name;
        public string to_name;
    }


    string Escape(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    string BuildJson(List<Node> nodes, List<Edge> edges)
    {
        StringBuilder sb = new StringBuilder();

        sb.Append("{\"nodes\":[");

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            sb.Append("{");
            sb.AppendFormat("\"id\":\"{0}\",", Escape(n.id.ToString()));
            sb.AppendFormat("\"name\":\"{0}\",", Escape(n.name));
            sb.AppendFormat("\"nickname\":\"{0}\",", Escape(n.nickname));
            sb.AppendFormat("\"type\":\"{0}\"", Escape(n.type));
            sb.Append("}");
            if (i < nodes.Count - 1) sb.Append(",");
        }

        sb.Append("],\"edges\":[");

        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            sb.Append("{");
            sb.AppendFormat("\"from\":\"{0}\",", Escape(e.from));
            sb.AppendFormat("\"to\":\"{0}\",", Escape(e.to));
            sb.AppendFormat("\"from_name\":\"{0}\",", Escape(e.from_name));
            sb.AppendFormat("\"to_name\":\"{0}\"", Escape(e.to_name));
            sb.Append("}");
            if (i < edges.Count - 1) sb.Append(",");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    string CallChatGPTBatch(string batchJson, string apiKey)
    {
        string url = "https://api.openai.com/v1/chat/completions";

        string payload =
            "{" +
            "\"model\":\"gpt-4.1-mini\"," +
            "\"messages\":[" +
            "{\"role\":\"system\",\"content\":\"You will receive multiple component-groups. For each group, generate exactly one 8–15 word engaging, motivating, but real and technical description. Return ONLY: { \\\"names\\\": [..] } where order matches the input groups.\"}," +
            "{\"role\":\"user\",\"content\":\"" + Escape(batchJson) + "\"}" +
            "]" +
            "}";

        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers.Add("Authorization", "Bearer " + apiKey);

            byte[] body = Encoding.UTF8.GetBytes(payload);
            using (var stream = req.GetRequestStream())
                stream.Write(body, 0, body.Length);

            using (var resp = req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return "{\"names\":[\"Error: " + ex.Message + "\"]}";
        }
    }

    // <Custom additional code> 

    static Dictionary<string, string> Sticky = new Dictionary<string, string>();

    string ExtractContentFromResponse(string raw)
    {
        // Very lightweight string search for: "content": "...."
        // inside the first choice.message block.

        const string marker = "\"content\":";
        int idx = raw.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        // Find the first quote after "content":
        int firstQuote = raw.IndexOf('"', idx + marker.Length);
        if (firstQuote < 0)
            return null;

        // The actual content starts after this quote
        int start = firstQuote + 1;

        // Find the closing quote – assumes no embedded quotes in content
        int end = raw.IndexOf('"', start);
        if (end < 0)
            return null;

        string content = raw.Substring(start, end - start);

        // Unescape common JSON escapes
        content = content.Replace("\\n", "\n").Replace("\\\"", "\"");

        return content;
    }
}


public static class GuessFactory
{
    public static CreateObjectItem[] MakeIntelligentGuesses(Guid OriginGUID, int trace, int predict)
    {
        // Retrieve the queries based on the OriginGUID
        LLMNamePredictor LLMHelper = new LLMNamePredictor();
        var guesses = ComponentTraversal.GetUpstreamResults(OriginGUID, trace, predict, out List<Guid> visited_guids);
        //List<string> generated_names = LLMHelper.GenerateBatchText("", expected_components, visited_guids);

        return guesses;
    }
}
