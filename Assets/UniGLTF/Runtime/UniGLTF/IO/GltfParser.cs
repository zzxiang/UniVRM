using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UniJSON;
using UnityEngine;

namespace UniGLTF
{
    public class GltfParser
    {
        /// <summary>
        /// JSON source
        /// </summary>
        public String Json;

        /// <summary>
        /// GLTF parsed from JSON
        /// </summary>
        public glTF GLTF;

        /// <summary>
        /// URI access
        /// </summary>
        public IStorage Storage;

        public static bool IsGeneratedUniGLTFAndOlderThan(string generatorVersion, int major, int minor)
        {
            if (string.IsNullOrEmpty(generatorVersion)) return false;
            if (generatorVersion == "UniGLTF") return true;
            if (!generatorVersion.FastStartsWith("UniGLTF-")) return false;

            try
            {
                var splitted = generatorVersion.Substring(8).Split('.');
                var generatorMajor = int.Parse(splitted[0]);
                var generatorMinor = int.Parse(splitted[1]);

                if (generatorMajor < major)
                {
                    return true;
                }
                else if (generatorMajor > major)
                {
                    return false;
                }
                else
                {
                    if (generatorMinor >= minor)
                    {
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarningFormat("{0}: {1}", generatorVersion, ex);
                return false;
            }
        }

        public bool IsGeneratedUniGLTFAndOlder(int major, int minor)
        {
            if (GLTF == null) return false;
            if (GLTF.asset == null) return false;
            return IsGeneratedUniGLTFAndOlderThan(GLTF.asset.generator, major, minor);
        }

        #region Parse
        public void ParsePath(string path)
        {
            Parse(path, File.ReadAllBytes(path));
        }

        /// <summary>
        /// Parse gltf json or Parse json chunk of glb
        /// </summary>
        /// <param name="path"></param>
        /// <param name="bytes"></param>
        public virtual void Parse(string path, Byte[] bytes)
        {
            var ext = Path.GetExtension(path).ToLower();
            switch (ext)
            {
                case ".gltf":
                    ParseJson(Encoding.UTF8.GetString(bytes), new FileSystemStorage(Path.GetDirectoryName(path)));
                    break;

                case ".zip":
                    {
                        var zipArchive = Zip.ZipArchiveStorage.Parse(bytes);
                        var gltf = zipArchive.Entries.FirstOrDefault(x => x.FileName.ToLower().EndsWith(".gltf"));
                        if (gltf == null)
                        {
                            throw new Exception("no gltf in archive");
                        }
                        var jsonBytes = zipArchive.Extract(gltf);
                        var json = Encoding.UTF8.GetString(jsonBytes);
                        ParseJson(json, zipArchive);
                    }
                    break;

                default:
                    ParseGlb(bytes);
                    break;
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="bytes"></param>
        public void ParseGlb(Byte[] bytes)
        {
            var chunks = glbImporter.ParseGlbChunks(bytes);

            if (chunks.Count != 2)
            {
                throw new Exception("unknown chunk count: " + chunks.Count);
            }

            if (chunks[0].ChunkType != GlbChunkType.JSON)
            {
                throw new Exception("chunk 0 is not JSON");
            }

            if (chunks[1].ChunkType != GlbChunkType.BIN)
            {
                throw new Exception("chunk 1 is not BIN");
            }

            try
            {
                var jsonBytes = chunks[0].Bytes;
                ParseJson(Encoding.UTF8.GetString(jsonBytes.Array, jsonBytes.Offset, jsonBytes.Count),
                    new SimpleStorage(chunks[1].Bytes));
            }
            catch (StackOverflowException ex)
            {
                throw new Exception("[UniVRM Import Error] json parsing failed, nesting is too deep.\n" + ex);
            }
            catch
            {
                throw;
            }
        }

        public virtual void ParseJson(string json, IStorage storage)
        {
            Json = json;
            Storage = storage;
            GLTF = GltfDeserializer.Deserialize(json.ParseAsJson());
            if (GLTF.asset.version != "2.0")
            {
                throw new UniGLTFException("unknown gltf version {0}", GLTF.asset.version);
            }

            // Version Compatibility
            RestoreOlderVersionValues();

            FixMeshNameUnique();
            FixImageNameUnique();
            FixMaterialNameUnique();
            FixNodeName();

            // parepare byte buffer
            //GLTF.baseDir = System.IO.Path.GetDirectoryName(Path);
            foreach (var buffer in GLTF.buffers)
            {
                buffer.OpenStorage(storage);
            }
        }

        void FixMeshNameUnique()
        {
            var used = new HashSet<string>();
            foreach (var mesh in GLTF.meshes)
            {
                if (string.IsNullOrEmpty(mesh.name))
                {
                    // empty
                    mesh.name = "mesh_" + Guid.NewGuid().ToString("N");
                    Debug.LogWarning($"no name: => {mesh.name}");
                    used.Add(mesh.name);
                }
                else
                {
                    var lower = mesh.name.ToLower();
                    if (used.Contains(lower))
                    {
                        // rename
                        var uname = lower + "_" + Guid.NewGuid().ToString("N");
                        Debug.LogWarning($"same name: {lower} => {uname}");
                        mesh.name = uname;
                        lower = uname;
                    }
                    used.Add(lower);
                }
            }
        }

        void FixImageNameUnique()
        {
            var used = new HashSet<string>();
            for (int i = 0; i < GLTF.images.Count; ++i)
            {
                var image = GLTF.images[i];
                if (string.IsNullOrEmpty(image.name))
                {
                    var newName = $"image_{i}";
                    if (!used.Add(newName))
                    {
                        newName = "image_" + Guid.NewGuid().ToString("N");
                        if (!used.Add(newName))
                        {
                            throw new Exception();
                        }
                    }
                    image.name = newName;
                    // Debug.LogWarning($"no name: => {image.name}");
                }
                else
                {
                    var lower = image.name.ToLower();
                    if (used.Contains(lower))
                    {
                        // rename
                        var uname = lower + "_" + Guid.NewGuid().ToString("N");
                        Debug.LogWarning($"same name: {lower} => {uname}");
                        image.name = uname;
                        lower = uname;
                    }
                    used.Add(lower);
                }
            }
        }

        public void FixMaterialNameUnique()
        {
            foreach (var material in GLTF.materials)
            {
                var originalName = material.name;
                int j = 2;
                while (GLTF.materials.Any(x => x != material && x.name == material.name))
                {
                    material.name = string.Format("{0}({1})", originalName, j++);
                }
            }
        }

        /// <summary>
        /// rename empty name to $"{index}"
        /// </summary>
        void FixNodeName()
        {
            for (var i = 0; i < GLTF.nodes.Count; ++i)
            {
                var node = GLTF.nodes[i];
                if (string.IsNullOrWhiteSpace(node.name))
                {
                    node.name = $"{i}";
                }
            }
        }

        void RestoreOlderVersionValues()
        {
            var parsed = UniJSON.JsonParser.Parse(Json);
            for (int i = 0; i < GLTF.images.Count; ++i)
            {
                if (string.IsNullOrEmpty(GLTF.images[i].name))
                {
                    try
                    {
                        var extraName = parsed["images"][i]["extra"]["name"].Value.GetString();
                        if (!string.IsNullOrEmpty(extraName))
                        {
                            //Debug.LogFormat("restore texturename: {0}", extraName);
                            GLTF.images[i].name = extraName;
                        }
                    }
                    catch (Exception)
                    {
                        // do nothing
                    }
                }
            }
        }
        #endregion

        public IEnumerable<GetTextureParam> EnumerateTextures()
        {
            for (int i = 0; i < GLTF.textures.Count; ++i)
            {
                yield return GetTextureParam.Create(GLTF, i);
            }

            // TODO: converted textures
        }
    }
}
