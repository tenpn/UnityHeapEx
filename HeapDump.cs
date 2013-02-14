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

        private readonly HashSet<object> seenObjects = new HashSet<object>();
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

            var allScripts = UnityEngine.Object.FindObjectsOfType( typeof( MonoBehaviour ) );

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


                var typeElement = doc.CreateElement("type");
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
            var scriptsElement = doc.CreateElement("scripts");
            doc.DocumentElement.AppendChild(scriptsElement);

            foreach( MonoBehaviour mb in allScripts )
            {
                var objectElement = doc.CreateElement("object");
                scriptsElement.AppendChild(objectElement);

                objectElement.SetAttribute("type", SecurityElement.Escape( mb.GetType().GetFormattedName() ));
                objectElement.SetAttribute("name", SecurityElement.Escape( mb.name ));

                var type = mb.GetType();
                int scriptSize = 0;
                foreach( var fieldInfo in type.EnumerateAllFields() )
                {
                    try
                    {
                        int size = ReportField( mb, fieldInfo, objectElement);
                        totalSize += size;
                        scriptSize += size;
                    }
                    catch( Exception ex )
                    {
                        Debug.LogError( "Exception: " + ex.Message + " on " + fieldInfo.FieldType.GetFormattedName() + " " +
                                        fieldInfo.Name );
                    }
                }
                
                objectElement.SetAttribute("totalsize", scriptSize.ToString());
            }

            string filename = "heapdump-" + DateTime.Now.ToString("s") + ".xml";

            using(var writer = new XmlTextWriter(filename, null))
            {
                writer.Formatting = Formatting.Indented;
                doc.Save(writer);
            }

            Debug.Log( "Written heap dump to file \"" + Path.GetFullPath(filename) + "\"");
        }
		
		/// <summary>
		/// Works through all fields of an object, dumpoing them into xml
		/// </summary>
        public int GatherFromRootRecursively(object root, XmlElement parent)
        {
            var seen = seenObjects.Contains( root );

            XmlElement rootElement = null;
            if( root is Object )
            {
                var uo = root as Object;
                rootElement = WriteUnityObjectData( uo, seen );
            }
            else
            {
                rootElement = doc.CreateElement("nonobject");
            }

            parent.AppendChild(rootElement);

            if( seen )
            {
                if(!(root is Object))
                {
					// XXX maybe add some object index so that this is traceable to original object dump
					// earlier in xml?
                    parent.AppendChild(doc.CreateElement("seen"));
                    
                }
                return 0;
            }

            seenObjects.Add( root );

            var type = root.GetType();
            var fields = type.EnumerateAllFields();
            var res = 0;
            foreach( var fieldInfo in fields )
            {
                res += ReportField( root, fieldInfo, rootElement);
            }

            rootElement.SetAttribute("totalsize", res.ToString());
            return res;
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
		
		/// <summary>
		/// Dumps info on the field in xml. Provides some rough estimate on size taken by its contents,
		/// and recursively enumerates fields if this one contains an object reference.
		/// </summary>
		/// <returns>
		/// Rough estimate of memory taken by field and its contents
		/// </returns>
        private int ReportField(object root, FieldInfo fieldInfo, XmlElement parent)
        {
            var v = fieldInfo.GetValue( root );
            int res = 0;
            var ftype = v==null?null:v.GetType();

            var elementOut = doc.CreateElement("field");
            elementOut.SetAttribute("type", SecurityElement.Escape( fieldInfo.FieldType.GetFormattedName() ));
            elementOut.SetAttribute("name", SecurityElement.Escape( fieldInfo.Name ));
            elementOut.SetAttribute("runtimetype", SecurityElement.Escape( v==null?"-null-":ftype.GetFormattedName()));
            parent.AppendChild(elementOut);

            if(v==null)
            {
                res += IntPtr.Size;
            }
            else if( ftype.IsArray )
            {
				// arrays have special treatment b/c we have to work on every array element
				// just like a single value.
				// TODO refactor this so that arry item and non-array field value share code
                var val = v as Array;
                res += IntPtr.Size; // reference size
                if( val != null && !seenObjects.Contains( val ))
                {
                    seenObjects.Add( val );
                    var length = GetTotalLength( val );

                    var arrayElement = doc.CreateElement("array");
                    arrayElement.SetAttribute("length", length.ToString());
                    elementOut.AppendChild(arrayElement);

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
                            Debug.LogError("error msg=\"Marshal.SizeOf() failed\"");
                        }
                    }
                    else if( eltype == typeof( string ) )
                    {
                        // special case
                        res += IntPtr.Size * length; // array itself

                        foreach( string item in val )
                        {
                            if( item != null )
                            {
                                var stringElement = doc.CreateElement("string");
                                stringElement.SetAttribute("length", item.Length.ToString());
                                arrayElement.AppendChild(stringElement);

                                if(!seenObjects.Contains( val ))
                                {
                                    seenObjects.Add( val );
                                    res += sizeof( char ) * item.Length + sizeof( int );
                                }
                            }
                        }
                    }
                    else
                    {
                        res += IntPtr.Size * length; // array itself
                        foreach( var item in val )
                        {
                            if( item != null )
                            {
                                var itemElement = doc.CreateElement("item");
                                arrayElement.AppendChild(itemElement);
                                itemElement.SetAttribute("type", SecurityElement.Escape( item.GetType().GetFormattedName() ));
                                
                                res += GatherFromRootRecursively( item, itemElement);
                            }
                        }
                    }
                }
                else
                {
                    var nullElement = doc.CreateElement("null");
                    elementOut.AppendChild(nullElement);
                }
            }
            else if( ftype.IsValueType )
            {
                if( ftype.IsPrimitive )
                {
                    var val = fieldInfo.GetValue( root );
                    res += Marshal.SizeOf( ftype );

                    var valueElement = doc.CreateElement("value");
                    elementOut.AppendChild(valueElement);
                    valueElement.SetAttribute("value", val.ToString());
                }
                else if( ftype.IsEnum )
                {
                    var val = fieldInfo.GetValue( root );
                    res += Marshal.SizeOf( Enum.GetUnderlyingType( ftype ) );

                    var valueElement = doc.CreateElement("value");
                    elementOut.AppendChild(valueElement);
                    valueElement.SetAttribute("value", val.ToString());
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
                        Debug.LogError("  <error msg=\"Marshal.SizeOf() failed\"/>" );
                    }
                    
                    var structElement = doc.CreateElement("struct");
                    elementOut.AppendChild(structElement);
                    structElement.SetAttribute("size", s.ToString());

                    res += s;
                }
            }
            else if( ftype == typeof( string ) )
            {
                // special case
                res += IntPtr.Size; // reference size
                var val = fieldInfo.GetValue( root ) as string;
                if( val != null )
                {
                    var stringElement = doc.CreateElement("string");
                    elementOut.AppendChild(stringElement);
                    elementOut.SetAttribute("length", val.Length.ToString());

                    if(!seenObjects.Contains( val ))
                    {
                        seenObjects.Add( val );
                        res += sizeof( char ) * val.Length + sizeof( int );
                    }
                }
                else
                {
                    var nullElement = doc.CreateElement("null");
                    elementOut.AppendChild(nullElement);
                }
            }
            else
            {
                // this is a reference 
                var classVal = fieldInfo.GetValue( root );
                res += IntPtr.Size; // reference size
                if( classVal != null )
                {
                    res += GatherFromRootRecursively( classVal, elementOut);
                }
                else
                {
                    var nullElement = doc.CreateElement("null");
                    elementOut.AppendChild(nullElement);
                }
            }

            var totalSizeElement = doc.CreateElement("total");
            totalSizeElement.SetAttribute("size", res.ToString());
            elementOut.AppendChild(totalSizeElement);

            elementOut.SetAttribute("totalsize", res.ToString());

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
    }
}