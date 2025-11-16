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
    public static string BuildDynamicPattern(List<string> names)
    {
        StringBuilder s = new StringBuilder();

        s.Append("MATCH ");
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0)
                s.Append($"-[r{i}]->");

            s.Append($"(node{i})");
        }

        s.Append("\nWHERE ");
        for (int i = 0; i < names.Count; i++)
        {
            s.Append($"node{i}.ComponentName = '{names[i]}'");
            if (i < names.Count - 1)
                s.Append(" AND ");
        }

        s.Append($"\nWITH node{names.Count - 1}");
        s.Append($"\nMATCH (node{names.Count - 1})-[rOut]->(next)");
        s.Append("\nRETURN DISTINCT next.ComponentGuid");

        return s.ToString();
    }
}

public static class ComponentTraversal
{
    public static void TraverseUpstream(IGH_DocumentObject obj, IDriver driver, HashSet<Guid> visited, List<Guid> guesses, List<string> currentChain, int depth)
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

                if (newChain.Count == depth)
                {
                    // Build and store the dynamic query for the current chain.
                    string query = DynamicCypherBuilder.BuildDynamicPattern(newChain);

                    // Run the query asynchronously and then block to get the result.
                    var cursor = driver.AsyncSession().RunAsync(query).GetAwaiter().GetResult();

                    // Retrieve all records returned by this query.
                    var records = cursor.ToListAsync().GetAwaiter().GetResult();

                    if (records.Count > 0)
                    {
                        foreach (var record in records)
                        {
                            guesses.Add(new Guid(record["next.ComponentGuid"].ToString()));
                        }
                    }
                }
                else
                {
                    // Continue traversing upstream.
                    TraverseUpstream(upstreamObj, driver, visited, guesses, newChain, depth);
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
                    if (newChain.Count == depth)
                    {
                        string query = DynamicCypherBuilder.BuildDynamicPattern(newChain);

                        // Run the query asynchronously and then block to get the result.
                        var cursor = driver.AsyncSession().RunAsync(query).GetAwaiter().GetResult();

                        // Retrieve all records returned by this query.
                        var records = cursor.ToListAsync().GetAwaiter().GetResult();

                        if (records.Count > 0)
                        {
                            foreach (var record in records)
                            {
                                guesses.Add(new Guid(record["next.ComponentGuid"].ToString()));
                            }
                        }
                    }
                    else
                    {
                        TraverseUpstream(upstreamObj, driver, visited, guesses, newChain, depth);
                    }
                }
            }
        }
    }

    public static List<Guid> GetUpstreamResults(Guid guid, int depth, out List<Guid> visitedGuids)
    {
        IGH_DocumentObject startObj = Instances.ActiveCanvas?.Document.FindObject(guid, false);
        if (startObj == null)
        {
            MessageBox.Show("Object not found in canvas");
            visitedGuids = new List<Guid>();
            return new List<Guid>();
        }

        IDriver driver = GraphDatabase.Driver(
            "neo4j+s://916f7f37.databases.neo4j.io",
            AuthTokens.Basic("neo4j", "_GjWi91K3QZkkGg3hA7Itrp-U9dlvzH80JnFsnvvW6I")
        );

        HashSet<Guid> visited = new HashSet<Guid>();
        List<Guid> guesses = new List<Guid>();
        List<string> initialChain = new List<string> { startObj.Name };
        TraverseUpstream(startObj, driver, visited, guesses, initialChain, depth);
        visitedGuids = visited.ToList();
        return guesses;
    }
}



public class GHHelpers
{
    public static IGH_DocumentObject GetObjectByGuid(string guidStr)
    {
        var doc = Grasshopper.Instances.ActiveCanvas?.Document;
        if (doc == null)
        {
            MessageBox.Show("Doc not found");
            return null;
        }

        if (!Guid.TryParse(guidStr, out Guid guid))
        {
            MessageBox.Show("Input not a GUID");
            return null;
        }

        return doc.FindObject(new Guid(guidStr), false);
    }

    public static List<Guid> RunQueries(List<string> queries)
    {
        IDriver driver = GraphDatabase.Driver(
            "neo4j+s://916f7f37.databases.neo4j.io",
            AuthTokens.Basic("neo4j", "_GjWi91K3QZkkGg3hA7Itrp-U9dlvzH80JnFsnvvW6I")
        );
        try
        {
            using (var session = driver.AsyncSession())
            {
                List<Guid> output = new List<Guid>();

                foreach (var q in queries)
                {
                    var cypherQuery = q.Trim();
                    var cursor = session.RunAsync(cypherQuery).GetAwaiter().GetResult();
                    var records = cursor.ToListAsync().GetAwaiter().GetResult();
                    foreach (var record in records)
                    {
                        if (record.Keys.Contains("next.ComponentGuid"))
                        {
                            output.Add(new Guid(record["next.ComponentGuid"].ToString()));
                        }
                    }
                }

                return output;
            }
        }
        catch (Exception ex)
        {
            // Optionally, throw or return an empty list if there is an error
            MessageBox.Show("Error: " + ex.Message);
            return new List<Guid>();
        }
        finally
        {
            driver.Dispose();
        }
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
            "{\"role\":\"system\",\"content\":\"You will receive multiple component-groups. For each group, generate exactly one 5–12 word engaging and motivating description. Return ONLY: { \\\"names\\\": [..] } where order matches the input groups.\"}," +
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
    public static CreateObjectItem[] MakeIntelligentGuesses(Guid OriginGUID)
    {
        // Retrieve the queries based on the OriginGUID
        LLMNamePredictor LLMHelper = new LLMNamePredictor();
        GHHelpers ghScript = new GHHelpers();

        // Get the Results from Neo4js
        List<Guid> expected_components = ComponentTraversal.GetUpstreamResults(OriginGUID, 2, out List<Guid> visited_guids);

        CreateObjectItem[] guesses = new CreateObjectItem[expected_components.Count];
        List<string> generated_names = LLMHelper.GenerateBatchText("", expected_components, visited_guids);
        ushort i = 0;
        foreach (Guid expected_guid in expected_components)
        {
            // Get the component from the GUID
            var _proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(expected_guid);
            if (_proxy == null)
            {
                guesses[i] = new CreateObjectItem(expected_guid, i, "Uninstalled component", false);
            }
            else
            {
                List<Guid> newList = new List<Guid>(visited_guids);
                newList.Add(expected_guid);

                guesses[i] = new CreateObjectItem(expected_guid, 0, generated_names[i], false);
            }
            i++;
        }
        return guesses;
    }
}
