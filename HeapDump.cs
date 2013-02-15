using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Linq;
using UnityEditor;
using UnityEngine;
using System.Xml;
using Object = UnityEngine.Object;

namespace UnityHeapEx
{
    public class HeapDump
    {
        [MenuItem( "Tools/Memory/HeapDump" )]
        public static void DoStuff()
        {
            var h = new HeapDump();
            h.DumpToXml();
        }

        private struct CachedObject
        {
            public CachedObject(Type type, XmlElement element, int size) : this()
            {
                Type = type;
                Element = element;
                Size = size;
            }

            public bool IsValid { get { return Type != null; } }

            public Type Type;
            public XmlElement Element;
            public int Size;
        }

        private Dictionary<System.Object, CachedObject> seenObjects = new Dictionary<System.Object, CachedObject>();
        private Queue<System.Object> instancesToProcess = new Queue<System.Object>();
        private XmlDocument doc = null;

        // when true, types w/o any static field (and skipped types) are removed from output
        public static bool SkipEmptyTypes = true;

        /* TODO
         * ignore cached delegates for lambdas(?)
         * better type names
         * deal with arrays of arrays
         * maybe special code for delegates? coroutines?
         */
		
		/// <summary>
		/// Collect all roots, i.e. static fields in all classes and all scripts; then dump all object 
		/// hierarchy reachable from these roots to file
		/// </summary>
        public void DumpToXml()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
			// assembles contain all referenced system assemblies, as well as UnityEngine, UnityEditor and editor
			// code. To make root list smaller, we only select main game assembly from the list, although it might
			// concievably miss some statics
            var gameAssembly = assemblies.Single( a => a.FullName.Contains( "Assembly-CSharp," ) );
            var allTypes = gameAssembly.GetTypes();

            var allGameObjects = UnityEngine.Object.FindObjectsOfType( typeof( GameObject ) );

            seenObjects.Clear(); // used to prevent going through same object twice

            doc = new XmlDocument();

            {
                var root = doc.CreateElement("dump");
                doc.AppendChild(root);
            }

            int totalSize = 0;
                
            var staticsElement = doc.CreateElement("statics");
            doc.DocumentElement.AppendChild(staticsElement);

            // enumerate all static fields
            foreach( var type in allTypes )
            {
                if (SkipEmptyTypes)
                {
                    // enums hold nothing but constants.
                    // generic types are ignored, because we can't access static fields unless we
                    // know actual type parameters of the class containing generics, and we have no way
                    // of knowing these - they depend on what concrete type were ever instantiated.
                    // This may miss a lot of stuff if generics are heavily used.
                    if (type.IsEnum || type.IsGenericType)
                    {
                        continue;
                    }
                }


                var typeElement = doc.CreateElement("statictype");
                staticsElement.AppendChild(typeElement);

                typeElement.SetAttribute("name", SecurityElement.Escape( type.GetFormattedName() ));

                if(type.IsEnum)
                {
                    var ignoredElement = doc.CreateElement("ignored");
                    typeElement.AppendChild(ignoredElement);
                    ignoredElement.SetAttribute("reason", "IsEnum");
                }
                else if(type.IsGenericType)
                {
                    var ignoredElement = doc.CreateElement("ignored");
                    typeElement.AppendChild(ignoredElement);
                    ignoredElement.SetAttribute("reason", "IsGenericType");
                }
                else
                {
                    int typeSize = 0;
                    foreach( var fieldInfo in type.GetFields( BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic ) )
                    {
                        int size = ReportField( null, fieldInfo, typeElement);
                        typeSize += size;
                        totalSize += size;
                    }

                    typeElement.SetAttribute("totalsize", typeSize.ToString());
                }
            }
				
            // enumerate all MonoBehaviours - that is, all user scripts on all existing objects.
            // TODO this maybe misses objects with active==false.
            var rootObjectsElement = doc.CreateElement("rootobjects");
            doc.DocumentElement.AppendChild(rootObjectsElement);

            foreach( GameObject go in allGameObjects)
            {
                if (go.transform.parent != null)
                {
                    continue;
                }

                ReportGameObject(go, rootObjectsElement);
            }

            var instancesElement = doc.CreateElement("instances");
            doc.DocumentElement.AppendChild(instancesElement);

            while(instancesToProcess.Count > 0)
            {
                var nextInstance = instancesToProcess.Dequeue();
                if (seenObjects.ContainsKey(nextInstance))
                {
                    // been done
                    continue;
                }

                ReportClassInstance(nextInstance, instancesElement);
            }

            SortElementsBySize(doc.DocumentElement);

            string filename = "heapdump-" + Path.GetFileNameWithoutExtension(EditorApplication.currentScene) + "-" 
                + DateTime.Now.ToString("s") + ".xml"; 

            using(var writer = new XmlTextWriter(filename, null))
            {
                writer.Formatting = Formatting.Indented;
                doc.Save(writer);
            }

            Debug.Log( "Written heap dump to file \"" + Path.GetFullPath(filename) + "\"");
        }

        private int ReportGameObject(GameObject go, XmlElement parent)
        {
            if (seenObjects.ContainsKey(go))
            {
                throw new UnityException("gameobj " + go.name + " has already been seen");
            }

            var rootObjectElement = doc.CreateElement("gameobject");
            parent.AppendChild(rootObjectElement);
            rootObjectElement.SetAttribute("name", go.name);
            // cache now so we don't get into a loop
            seenObjects[go] = new CachedObject(go.GetType(), rootObjectElement, -1);

            int goSize = 0;

            // report children
            {
                int childrenSize = 0;
                var childrenElement = doc.CreateElement("childobjects");
                rootObjectElement.AppendChild(childrenElement);

                // don't process transform directly, but do process child objects
                foreach(Transform childTransform in go.transform)
                {
                    int childSize = ReportGameObject(childTransform.gameObject, childrenElement);
                    childrenSize += childSize;
                }

                childrenElement.SetAttribute("totalsize", childrenSize.ToString());

                goSize += childrenSize;
            }


            foreach(var component in go.GetComponents<Component>())
            {
                if (component is Transform)
                {
                    continue;
                }

                if (seenObjects.ContainsKey(component))
                {
                    var seenObj = seenObjects[component];
                    rootObjectElement.AppendChild(CreateSeenElement(seenObj));
                    goSize += seenObj.Size;
                }
                else
                {
                    goSize += ReportClassInstance(component, rootObjectElement);
                }
            }

            // correctly report size
            seenObjects[go] = new CachedObject(go.GetType(), rootObjectElement, goSize);

            rootObjectElement.SetAttribute("totalsize", goSize.ToString());
            return goSize;
        }
		
        private XmlElement WriteUnityObjectData( Object uo, bool seen )
        {
			// shows some additional info on UnityObjects
            var unityObjectElement = doc.CreateElement("unityObject");
            unityObjectElement.SetAttribute("type", SecurityElement.Escape( uo.GetType().GetFormattedName() ));
            unityObjectElement.SetAttribute("name", SecurityElement.Escape( uo ? uo.name : "--missing reference--" ));
            unityObjectElement.SetAttribute("seedn", seen.ToString());

            // todo we can show referenced assets for renderers, materials, audiosources etc
            return unityObjectElement;
        }

        private XmlElement CreateNullElement()
        {
            return doc.CreateElement("null");
        }

        private XmlElement CreateSeenElement(CachedObject seenObj)
        {
            if (seenObj.IsValid == false)
            {
                throw new UnityException("seen obj was null");
            }

            var seenElement = doc.CreateElement("seenobject");
            seenElement.SetAttribute("size", seenObj.Size.ToString());
            seenElement.SetAttribute("type", seenObj.Type.ToString());

            return seenElement;
        }

        private int ReportValue(System.Object obj, XmlElement parent)
        {
            if (obj == null)
            {
                parent.AppendChild(CreateNullElement());
                return IntPtr.Size;
            }

            var ftype = obj.GetType();
            
            XmlElement valueElement = null;
            int res = 0;

            if( ftype.IsValueType )
            {
                if( ftype.IsPrimitive )
                {
                    res += Marshal.SizeOf( ftype );

                    valueElement = doc.CreateElement("value");
                    valueElement.SetAttribute("value", obj.ToString());
                }
                else if( ftype.IsEnum )
                {
                    res += Marshal.SizeOf( Enum.GetUnderlyingType( ftype ) );

                    valueElement = doc.CreateElement("enum");
                    valueElement.SetAttribute("value", obj.ToString());
                }
                else
                {
					// this is a struct. This code assumes that all structs contain only primitive types,
					// which is very strong. Structs that contain references will break, and fail to traverse these
					// references properly
                    int s = 0;
                    try
                    {
                        s = Marshal.SizeOf( ftype );
                    }
                    catch( Exception )
                    {
                        // this breaks if struct has a reference member. We should probably never have such structs, but we'll see...
                        Debug.LogError("error Marshal.SizeOf() of type " + ftype + " failed" );
                    }

                    res += s;
                }
            }
            else if( ftype == typeof( string ) )
            {
                // special case
                res += IntPtr.Size; // reference size
                var val = obj as string;

                valueElement = doc.CreateElement("string");
                valueElement.SetAttribute("length", val.Length.ToString());

                res += sizeof( char ) * val.Length + sizeof( int );
            }
            else if (seenObjects.ContainsKey(obj))
            {
                var seenObj = seenObjects[obj];
                parent.AppendChild(CreateSeenElement(seenObj));
                return seenObj.Size;
            }
            else if( ftype.IsArray )
            {
				// arrays have special treatment b/c we have to work on every array element
				// just like a single value.
                var val = obj as Array;
                res += IntPtr.Size; // reference size

                var length = GetTotalLength( val );

                var arrayElement = doc.CreateElement("array");
                arrayElement.SetAttribute("length", length.ToString());
                parent.AppendChild(arrayElement);

                var eltype = ftype.GetElementType();
                if( eltype.IsValueType )
                {
                    if( eltype.IsEnum )
                        eltype = Enum.GetUnderlyingType( eltype );
                    try
                    {
                        res += Marshal.SizeOf( eltype ) * length;
                    }
                    catch( Exception )
                    {
                        Debug.LogError("error msg=\"Marshal.SizeOf() failed for type " + eltype + "\"");
                    }
                }
                else
                {
                    foreach(var arrObj in val)
                    {
                        res += ReportValue(arrObj, arrayElement);
                    }
                }

                valueElement = arrayElement;
            }
            else
            {
                // this is a reference 
                res += IntPtr.Size; // reference size
                instancesToProcess.Enqueue(obj);
                valueElement = CreateReferenceElement();
            }

            if (valueElement != null)
            {
                parent.AppendChild(valueElement);

                seenObjects[obj] = new CachedObject(ftype, valueElement, res);
            }

            return res;
        }

        private XmlElement CreateReferenceElement()
        {
            return doc.CreateElement("reference");
        }

        private int ReportClassInstance(object instance, XmlElement parent)
        {
            if (seenObjects.ContainsKey(instance))
            {
                throw new UnityException("instance " + instance + " already in seen objects cache");
            }

            var type = instance.GetType();

            var instanceElement = doc.CreateElement("instance");
            parent.AppendChild(instanceElement);
            instanceElement.SetAttribute("type", SecurityElement.Escape( type.GetFormattedName() ));

            // reserve as seen so we don't get into a loop
            seenObjects[instance] = new CachedObject(type, instanceElement, -1);           
            
            int instanceSize = 0;
            foreach( var fieldInfo in type.EnumerateAllFields() )
            {
                try
                {
                    int size = ReportField( instance, fieldInfo, instanceElement);
                    instanceSize += size;
                }
                catch( Exception ex )
                {
                    Debug.LogError( "Exception: " + ex.Message + " on " + fieldInfo.FieldType.GetFormattedName() + " " +
                                    fieldInfo.Name );
                }
            }

            instanceElement.SetAttribute("totalsize", instanceSize.ToString());
            
            // now store correct size
            seenObjects[instance] = new CachedObject(type, instanceElement, instanceSize);

            return instanceSize;
        }
		
		/// <summary>
		/// Dumps info on the field in xml. Provides some rough estimate on size taken by its contents,
		/// and recursively enumerates fields if this one contains an object reference.
		/// </summary>
		/// <returns>
		/// Rough estimate of memory taken by field and its contents
		/// </returns>
        private int ReportField(object root, FieldInfo fieldInfo, XmlElement parent)
        {
            var fieldElement = doc.CreateElement("field");
            fieldElement.SetAttribute("type", SecurityElement.Escape( fieldInfo.FieldType.GetFormattedName() ));
            fieldElement.SetAttribute("name", SecurityElement.Escape( fieldInfo.Name ));

            parent.AppendChild(fieldElement);

            var v = fieldInfo.GetValue( root );
            int res = ReportValue(v, fieldElement);
            
            fieldElement.SetAttribute("totalsize", res.ToString());

            return res;
        }

        private int GetTotalLength(Array val)
        {
            var rank = val.Rank;
            if( rank == 1 )
                return val.Length;

            var l = 1;
            while( rank > 0 )
            {
                l *= val.GetLength( rank - 1 );
                rank--;
            }
            return l;
        }

        // finds totalsize/size attribute on child elements, places them in order.
        // operates recursively on all children
        private void SortElementsBySize(XmlElement element)
        {
            var childrenToSort = new List<Pair<int,XmlElement>>();
            foreach(var child in element)
            {
                var childElement = child as XmlElement;
                if (childElement == null)
                {
                    continue;
                }

                SortElementsBySize(childElement);

                string sizeString = childElement.GetAttribute("totalsize");
                if (sizeString == "")
                {
                    sizeString = childElement.GetAttribute("size");
                }

                if (sizeString == "")
                {
                    continue;
                }

                int sizeValue = int.Parse(sizeString);
                childrenToSort.Add(new Pair<int, XmlElement>(sizeValue, childElement));
            }

            // remove all found children from parent
            foreach(var sizeChild in childrenToSort)
            {
                element.RemoveChild(sizeChild.Second);
            }

            var sortedChildren = childrenToSort.OrderBy(c => c.First).Reverse().Select(c => c.Second);
            foreach(var sortedChild in sortedChildren)
            {
                element.AppendChild(sortedChild);
            }
        }
    }
}