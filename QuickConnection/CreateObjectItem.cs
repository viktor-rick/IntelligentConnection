using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

namespace QuickConnection;

public class CreateObjectItem : IComparable<CreateObjectItem>
{
    public readonly static MethodInfo functions = typeof(GH_Canvas).GetRuntimeMethods().Where(m => m.Name.Contains("InstantiateNewObject") && !m.IsPublic).First();

    public ushort Index { get; }
    public string InputParamName { get; set; }
    public string OutputParamName { get; set; }
    public Guid ObjectGuid { get; }
    public string InitString { get; set; }
    public Bitmap Icon { get; } = null;
    public string ShowName { get; } = "Not Found";
    public string Name { get; } = "Not Found";
    public bool IsInput { get; }

    private readonly IGH_ObjectProxy _proxy;

    private readonly bool isCoreLibrary = false;
    public CreateObjectItem[] MultiItems { get; set; } = null;

    public CreateObjectItem(Guid guid, ushort index, string init, bool isInput)
    {
        ObjectGuid = guid;
        InitString = init;
        Index = index;
        IsInput = isInput;

        _proxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(guid);
        if(_proxy == null)
        {
            foreach (var proxy in Grasshopper.Instances.ComponentServer.ObjectProxies)
            {
                if(proxy.LibraryGuid == ObjectGuid)
                {
                    _proxy = proxy;
                    break;
                }
            }
        }

        foreach (var assembly in Grasshopper.Instances.ComponentServer.Libraries)
        {

            if (_proxy.LibraryGuid == assembly.Id)
            {
                isCoreLibrary = assembly.IsCoreLibrary;
                break;
            }
        }

        if (_proxy == null) return;

        Icon = _proxy.Icon;
        Name = _proxy.Desc.Name;
        ShowName = $"{_proxy.Desc.Name}[{index}] - {_proxy.Desc.Description}";
    }

    public static void CreateMultiple(
    IGH_Param owner,
    PointF pos,
    CreateObjectItem[] items)
    {
        if (items == null || items.Length == 0)
            return;

        IGH_Param currentInput = owner;
        float dx = 0;

        foreach (var item in items)
        {
            var dropPos = new PointF(pos.X + dx, pos.Y);

            IGH_DocumentObject newObj =
                item.CreateObject(currentInput, dropPos);

            if (newObj is IGH_Component comp)
            {
                // next item will connect to this component’s output
                currentInput = comp.Params.Output[item.Index];
            }
            else if (newObj is IGH_Param par)
            {
                // next item will connect to this param's output grip
                currentInput = par;
            }

            dx += 110; // spacing for display
        }

        Grasshopper.Instances.ActiveCanvas.Document.NewSolution(false);
    }

    public CreateObjectItem(CreateObjectItemSave save, bool isInput):this(save.ObjectGuid, save.Index, save.InitString, isInput)
    {

    }

    public IGH_DocumentObject CreateObject(IGH_Param param, PointF objCenter, Action<IGH_DocumentObject> action = null)
    {
        IGH_DocumentObject obj = _proxy?.CreateInstance();
        if (obj == null) return null;

        action?.Invoke(obj);

        if (obj is IGH_Component)
        {
            IGH_Component com = (IGH_Component)obj;
            AddAObjectToCanvas(obj, objCenter, InitString);

            // ---------- NEW INDEX / NAME-BASED LOOKUP ----------
            string chosenName = null;

            if (IsInput)
                chosenName = OutputParamName;
            else
                chosenName = InputParamName;

            int idx;

            if (!string.IsNullOrEmpty(chosenName))
            {
                idx = FindParamIndex(com, chosenName, IsInput);
            }
            else
            {
                idx = Index;
            }
            // ----------------------------------------------------

            if (IsInput)
            {
                param.AddSource(com.Params.Output[idx]);
            }
            else
            {
                com.Params.Input[idx].AddSource(param);
            }

            Grasshopper.Instances.ActiveCanvas.Document.NewSolution(false);
        }
        else if (obj is IGH_Param)
        {
            IGH_Param par = obj as IGH_Param;
            AddAObjectToCanvas(obj, objCenter, InitString);

            if (IsInput)
            {
                param.AddSource(par);
            }
            else
            {
                par.AddSource(param);
            }

            Grasshopper.Instances.ActiveCanvas.Document.NewSolution(false);
        }

        return obj;
    }

    private static int FindParamIndex(IGH_Component comp, string paramName, bool isInput)
    {
        if (comp == null)
            throw new ArgumentNullException(nameof(comp));

        // choose input or output list
        var list = isInput ? comp.Params.Output : comp.Params.Input;

        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, paramName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(list[i].NickName, paramName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentException(
            $"Parameter '{paramName}' not found on component '{comp.Name}' " +
            $"({(isInput ? "output" : "input")} params).");
    }

    public static void AddAObjectToCanvas(IGH_DocumentObject obj, PointF pivot, string init, bool update = false)
    {
        functions.Invoke(Grasshopper.Instances.ActiveCanvas, new object[] { obj, init, pivot, update });
    }

    public int CompareTo(CreateObjectItem other)
    {
        IGH_ObjectProxy thisProxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(this.ObjectGuid);
        IGH_ObjectProxy otherProxy = Grasshopper.Instances.ComponentServer.EmitObjectProxy(other.ObjectGuid);

        int compareRound1 = (other.isCoreLibrary).CompareTo(isCoreLibrary);

        if(compareRound1 != 0) return compareRound1;

        if (thisProxy.Desc.HasCategory && otherProxy.Desc.HasCategory)
        {
            int compareRound1_1 = thisProxy.Desc.Category.CompareTo(otherProxy.Desc.Category);
            if (compareRound1_1 != 0) return compareRound1_1;
        }

        if(thisProxy.Desc.HasSubCategory && otherProxy.Desc.HasSubCategory)
        {
            int compareRound2 = thisProxy.Desc.SubCategory.CompareTo(otherProxy.Desc.SubCategory);
            if (compareRound2 != 0) return compareRound2;
        }

        return thisProxy.Desc.Name.CompareTo(otherProxy.Desc.Name);
    }
}

public struct CreateObjectItemSave
{
    public Guid ObjectGuid { get; set; }
    public string InitString { get; set; }
    public ushort Index { get; set; }

    public CreateObjectItemSave(Guid guid, string init, ushort index)
    {
        ObjectGuid = guid;
        InitString = init;
        Index = index;
    }

    public CreateObjectItemSave(CreateObjectItem item)
    {
        ObjectGuid = item.ObjectGuid;
        InitString = item.InitString;
        Index = item.Index;
    }
}
