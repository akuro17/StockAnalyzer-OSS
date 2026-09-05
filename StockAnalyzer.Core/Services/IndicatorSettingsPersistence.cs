using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services
{
    public class IndicatorSettingsPersistence
    {
        private const int MaxBackups = 5;
        private const int CurrentVersion = 1;

        public async Task SaveAsync(IEnumerable<ICoreIndicator> indicators, string filePath)
        {
            var container = new IndicatorSettingsContainer
            {
                Version = CurrentVersion,
                Settings = indicators.Select(indicator =>
                {
                    var setting = new IndicatorSetting
                    {
                        TypeName = indicator.GetType().AssemblyQualifiedName!
                    };

                    var properties = indicator.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead && p.CanWrite && p.DeclaringType != typeof(CoreIndicatorBase));

                    foreach (var prop in properties)
                    {
                        setting.Parameters[prop.Name] = prop.GetValue(indicator)!;
                    }

                    return setting;
                }).ToList()
            };

            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tmpFilePath = Path.Combine(directory ?? string.Empty, Guid.NewGuid().ToString() + ".tmp");

            try
            {
                using (var stream = new FileStream(tmpFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, container, new JsonSerializerOptions { WriteIndented = true });
                }

                if (File.Exists(filePath))
                {
                    RotateBackups(filePath, directory);
                    string backupPath = filePath + ".bak1";
                    File.Copy(filePath, backupPath, true);
                }

                if (File.Exists(filePath))
                {
                    File.Replace(tmpFilePath, filePath, null);
                }
                else
                {
                    File.Move(tmpFilePath, filePath);
                }
            }
            finally
            {
                if (File.Exists(tmpFilePath))
                {
                    File.Delete(tmpFilePath);
                }
            }
        }

        public async Task<IndicatorSettingsContainer> LoadAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new IndicatorSettingsContainer();
            }

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var container = await JsonSerializer.DeserializeAsync<IndicatorSettingsContainer>(stream);
                    return container ?? new IndicatorSettingsContainer();
                }
            }
            catch (JsonException)
            {
                // Handle deserialization error, maybe load a backup? For now, return default.
                return new IndicatorSettingsContainer();
            }
        }
        
        private void RotateBackups(string filePath, string? directory)
        {
            string baseBackupPath = filePath + ".bak";

            // Delete the oldest backup if it exists
            string oldestBackup = baseBackupPath + MaxBackups;
            if (File.Exists(oldestBackup))
            {
                File.Delete(oldestBackup);
            }

            // Shift existing backups
            for (int i = MaxBackups - 1; i >= 1; i--)
            {
                string currentBackup = baseBackupPath + i;
                string nextBackup = baseBackupPath + (i + 1);
                if (File.Exists(currentBackup))
                {
                    File.Move(currentBackup, nextBackup);
                }
            }
        }
    }
}
