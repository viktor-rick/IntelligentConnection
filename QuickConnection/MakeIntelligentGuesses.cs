using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Neo4j.Driver;
using Rhino;
using System;
using System.Collections.Generic;
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
    public static void TraverseUpstream(
        IGH_DocumentObject obj,
        HashSet<Guid> visited,
        List<string> queries,
        List<string> chain)
    {
        if (obj == null)
        {
            return;
        }
        if (visited.Contains(obj.ComponentGuid))
        {
            return;
        }

        visited.Add(obj.ComponentGuid);

        if (obj is IGH_Param param)
        {
            foreach (IGH_Param source in param.Sources)
            {
                IGH_DocumentObject up = source.Attributes.GetTopLevel.DocObject;
                List<string> newChain = new List<string> { up.Name };
                newChain.AddRange(chain);

                queries.Add(DynamicCypherBuilder.BuildDynamicPattern(newChain));
                TraverseUpstream(up, visited, queries, newChain);
            }
        }
        else if (obj is GH_Component comp)
        {
            foreach (IGH_Param p in comp.Params.Input)
            {
                foreach (IGH_Param source in p.Sources)
                {
                    IGH_DocumentObject up = source.Attributes.GetTopLevel.DocObject;
                    List<string> newChain = new List<string> { up.Name };
                    newChain.AddRange(chain);

                    queries.Add(DynamicCypherBuilder.BuildDynamicPattern(newChain));
                    TraverseUpstream(up, visited, queries, newChain);
                }
            }
        }
    }

    public static List<string> GetUpstreamQueries(IGH_DocumentObject obj)
    {
        HashSet<Guid> visited = new HashSet<Guid>();
        List<string> queries = new List<string>();
        List<string> chain = new List<string> { obj.Name };

        TraverseUpstream(obj, visited, queries, chain);

        return queries;
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

    public List<string> GetQueries(string guidStr)
    {
        IGH_DocumentObject obj = GetObjectByGuid(guidStr);
        if (obj == null)
        {
            MessageBox.Show("Object not found in canvas");
            return new List<string>();
        }
        return ComponentTraversal.GetUpstreamQueries(obj);
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


    public static class GuessFactory
{
    public static CreateObjectItem[] MakeIntelligentGuesses(Guid OriginGUID)
    {
        // Retrieve the queries based on the OriginGUID
        GHHelpers ghScript = new GHHelpers();
        List<string> queries = ghScript.GetQueries(OriginGUID.ToString());
        
        // Retrieve the guessed GUIDs from the database
        List<Guid> expected_components = GHHelpers.RunQueries(queries);

        CreateObjectItem[] guesses = new CreateObjectItem[expected_components.Count];
        ushort i = 0;
        foreach (Guid expected_guid in expected_components)
        {
            // Get the component from the GUID

            IGH_DocumentObject found_component = GHHelpers.GetObjectByGuid(expected_guid.ToString());
            var _proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(expected_guid);
            if (_proxy == null)
            {
                guesses[i] = new CreateObjectItem(expected_guid, i, "Uninstalled component", false);
            }
            else
            {
                guesses[i] = new CreateObjectItem(expected_guid, i, _proxy.Desc.Name, false);
            }
            i++;
        }
        return guesses;
    }
}
