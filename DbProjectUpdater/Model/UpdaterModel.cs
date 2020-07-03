using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Build.Evaluation;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace DbProjectUpdater.Model
{
    public class UpdaterModel
    {
        public Server Server { get; set; }

        public Database Db { get; set; }

        public Project DbProject { get; set; }

        private readonly Dictionary<Type, string> _directoryNames = new Dictionary<Type, string>
        {
            { typeof(StoredProcedure), "Stored Procedures" },
            { typeof(UserDefinedFunction), "Functions" },
            { typeof(Microsoft.SqlServer.Management.Smo.View), "Views" },
            { typeof(Table), "Tables" },
            { typeof(Trigger), "Triggers" },
        };

        public void UpdateDbProject(IProgress<(int Step, int DbObjectsNumber)> progress)
        {
            SetServerInitSystemProperty();

            var dbObjectCollection = new List<SmoCollectionBase>()
            {
                Db.StoredProcedures,
                Db.UserDefinedFunctions,
                Db.Views,
                Db.Tables
            };

            dbObjectCollection.AddRange(Db.Tables.Cast<Table>().Select(t => t.Triggers));
            dbObjectCollection.AddRange(Db.Views.Cast<Microsoft.SqlServer.Management.Smo.View>().Select(t => t.Triggers));

            var dbObjects = dbObjectCollection.SelectMany(c => c.Cast<ScriptNameObjectBase>())
                .Where(obj => (bool)obj.Properties["IsSystemObject"].Value == false);

            ScriptingOptions defaultScriptOptions = new ScriptingOptions()
            {
                IncludeDatabaseContext = true,
                ContinueScriptingOnError = true,
                DriAll = true,
                Indexes = true,
                Encoding = Encoding.UTF8,
                ToFileOnly = true
            };

            int dbObjectsNumber = dbObjects.Count();

            UpdateDbProjectStructure();

            var projectLock = new object();

            Parallel.ForEach(
                source: dbObjects,
                localInit: () => new Server(Server.Name),
                body: (obj, _, server) =>
                {
                    string schema = ((obj is Trigger tr ? tr.Parent : obj) as ScriptSchemaObjectBase).Schema;
                    string objName = Regex.Replace(obj.Name, $"[{new string(Path.GetInvalidFileNameChars())}]", "_");
                    string fileName = Path.Combine(schema, _directoryNames[obj.GetType()], $"{objName}.sql");

                    var scriptOptions = new ScriptingOptions(defaultScriptOptions)
                    {
                        FileName = Path.Combine(DbProject.DirectoryPath, fileName)
                    };

                    var scripter = new Scripter(server)
                    {
                        Options = scriptOptions
                    };

                    scripter.Script(new Urn[] { obj.Urn });

                    if (!(obj is Table))
                    {
                        string script = File.ReadAllText(scriptOptions.FileName, Encoding.UTF8);
                        script = Regex.Replace(script, "CREATE(?= (PROC|FUNCTION|VIEW|TRIGGER))", "ALTER", RegexOptions.IgnoreCase);
                        File.WriteAllText(scriptOptions.FileName, script, Encoding.UTF8);
                    }

                    if (DbProject.GetItemsByEvaluatedInclude(fileName).Count == 0)
                    {
                        lock (projectLock)
                        {
                            DbProject.AddItem("Build", fileName);
                        }
                    }

                    progress.Report((Step: 1, DbObjectsNumber: dbObjectsNumber));

                    return server;
                },
                localFinally: server => { });

            DbProject.Save();
        }

        private void SetServerInitSystemProperty()
        {
            var types = new Type[]
            {
                typeof(StoredProcedure),
                typeof(UserDefinedFunction),
                typeof(Microsoft.SqlServer.Management.Smo.View),
                typeof(Table),
                typeof(Trigger),
                typeof(Schema)
            };

            foreach (var type in types)
            {
                Server.SetDefaultInitFields(type, "IsSystemObject");
            }
        }

        private void UpdateDbProjectStructure()
        {
            string dir;

            foreach (var schema in Db.Schemas.Cast<Schema>().Where(s => !s.IsSystemObject || s.Name == "dbo"))
            {
                dir = schema.Name + Path.DirectorySeparatorChar;

                AddDbProjectFolder(dir);

                foreach (var dirName in _directoryNames.Values)
                {
                    dir = Path.Combine(schema.Name, dirName) + Path.DirectorySeparatorChar;

                    Directory.CreateDirectory(Path.Combine(DbProject.DirectoryPath, dir));
                    AddDbProjectFolder(dir);
                }
            }
        }

        private void AddDbProjectFolder(string dir)
        {
            if (DbProject.GetItemsByEvaluatedInclude(dir).Count == 0)
            {
                DbProject.AddItem("Folder", dir);
            }
        }
    }
}
