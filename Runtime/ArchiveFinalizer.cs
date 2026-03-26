using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace SensorFlex.Recorder
{
    public static class ArchiveFinalizer
    {
        public static void FinalizeArchive(string sourceDirectory, string archiveOutputPath)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                Debug.LogError($"[ArchiveFinalizer] Source directory {sourceDirectory} does not exist.");
                return;
            }

            try
            {
                if (File.Exists(archiveOutputPath))
                {
                    File.Delete(archiveOutputPath);
                }

                // Compress directory
                ZipFile.CreateFromDirectory(
                    sourceDirectory,
                    archiveOutputPath,
                    System.IO.Compression.CompressionLevel.Fastest,
                    true);
                Debug.Log($"[ArchiveFinalizer] Successfully created archive at {archiveOutputPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ArchiveFinalizer] Failed to compress capture to zip: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}
