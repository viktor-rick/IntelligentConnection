using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Neo4j.Driver;
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
    public string GenerateText(string ApiKey, List<string> AllNames)
    {
        //var doc = Grasshopper.Instances.ActiveCanvas?.Document;

        //List<Node> nodes = new List<Node>();
        //List<Edge> edges = new List<Edge>();

        ////// ---------------------------------------
        ////// Collect nodes
        ////// ---------------------------------------
        ////if (doc != null)
        ////{
        ////    List<GH_Component> comps = new List<GH_Component>();


        //foreach (IGH_DocumentObject obj in doc.Objects)
        //{
        //    GH_Component comp = obj as GH_Component;
        //    if (comp == null) continue;
        //    if (!comp.Attributes.Selected) continue;

        //    comps.Add(comp);
        //}

        //Dictionary<GH_Component, string> map = new Dictionary<GH_Component, string>();

        //foreach (GH_Component c in comps)
        //{
        //    string id = c.InstanceGuid.ToString();
        //    map[c] = id;

        //    nodes.Add(new Node
        //    {
        //        id = id,
        //        name = c.Name,
        //        nickname = c.NickName,
        //        type = c.GetType().FullName
        //    });
        //}

        //// ---------------------------------------
        //// Collect edges
        //// ---------------------------------------
        //foreach (GH_Component target in comps)
        //{
        //    string targetId = map[target];

        //    foreach (IGH_Param input in target.Params.Input)
        //    {
        //        foreach (IGH_Param src in input.Sources)
        //        {
        //            if (src == null || src.Attributes == null) continue;

        //            var top = src.Attributes.GetTopLevel;
        //            if (top == null) continue;

        //            GH_Component fromC = top.DocObject as GH_Component;
        //            if (fromC == null) continue;
        //            if (fromC.InstanceGuid == this.Component.InstanceGuid) continue;

        //            string fromId;
        //            if (!map.TryGetValue(fromC, out fromId)) continue;

        //            edges.Add(new Edge
        //            {
        //                from = fromId,
        //                to = targetId,
        //                from_name = fromC.NickName,
        //                to_name = target.NickName
        //            });
        //        }
        //    }
        //}


        // Output lists
        //NODES = nodes;
        //EDGES = edges;

        // ---------------------------------------
        // MANUAL JSON SERIALIZATION
        // ---------------------------------------
        //string json = BuildJson(nodes, edges);


        // ---------------------------------------
        // TEMP
        // ---------------------------------------

        StringBuilder sb = new StringBuilder();

        sb.Append("{\"nodes\":[");
        for (int i = 0; i < AllNames.Count; i++)
        {
            var name = AllNames[i];
            sb.Append("{");
            sb.AppendFormat("\"id\":\"{0}\",", Escape(i.ToString()));
            sb.AppendFormat("\"name\":\"{0}\",", Escape(name));
            sb.AppendFormat("\"nickname\":\"{0}\",", Escape(name));
            sb.AppendFormat("\"type\":\"{0}\"", Escape("Grasshopper.Kernel.Parameters.Param_GenericObject"));
            sb.Append("}");
            if (i < AllNames.Count - 1) sb.Append(",");
        }
        string json = sb.ToString();

        // ---------------------------------------
        // ChatGPT call
        // ---------------------------------------

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            MessageBox.Show("Please provide an API key");
        }

        string response = CallChatGPT(json, ApiKey);
        return response;
    }

    public class Node
    {
        public string id;
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
            sb.AppendFormat("\"id\":\"{0}\",", Escape(n.id));
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

    string CallChatGPT(string graphJson, string apiKey)
    {
        string url = "https://api.openai.com/v1/chat/completions";

        string payload =
            "{" +
            "\"model\":\"gpt-4.1-mini\"," +
            "\"messages\":[" +
            "{\"role\":\"system\",\"content\":\"You are a helpful assistant for Grasshopper in Rhino. You will get a json format grasshopper file component data - output a very brief just a single line only 5 to 12 words on what it does.\"}," +
            "{\"role\":\"user\",\"content\":\"" + Escape(graphJson) + "\"}" +
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

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
            {
                string raw = reader.ReadToEnd();

                // ------------------------------------
                // Extract choices[0].message.content
                // ------------------------------------
                string content = ExtractContentFromResponse(raw);
                return content ?? raw; // fallback to raw if parsing fails
            }
        }
        catch (Exception ex)
        {
            return "Error: " + ex.Message;
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

        // Get the names of the visited components
        List<string> names = new List<string>();
        foreach (Guid v in visited_guids)
        {
            IGH_DocumentObject original_component = GHHelpers.GetObjectByGuid(v.ToString());
            names.Add(original_component.Name);
        }

        CreateObjectItem[] guesses = new CreateObjectItem[expected_components.Count];
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
                List<string> newList = new List<string>(names);
                newList.Add(_proxy.Desc.Name);

                string generated_name = LLMHelper.GenerateText("", newList);

                guesses[i] = new CreateObjectItem(expected_guid, 0, generated_name, false);
            }
            i++;
        }
        return guesses;
    }
}
