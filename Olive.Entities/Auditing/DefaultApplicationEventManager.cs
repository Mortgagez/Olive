using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Olive.Entities
{
    /// <summary>
    /// This class provides default services for application events and general logging.
    /// </summary>
    public class DefaultApplicationEventManager
    {
        static Type ConcreteApplicationEventType;
        static Func<IPrincipal> GetUserPrincipal;
        static Func<string> GetUserIP;

        static DefaultApplicationEventManager()
        {
            var assemblyName = Config.GetOrThrow("Database:Initializer.Container.Assembly.Filename", "Olive.Web.dll");

            var assembly = AppDomain.CurrentDomain.LoadAssembly(assemblyName);
            InitializerAttribute.InvokeInitializeMethod<UserInfoAccessorInitializerAttribute>(assembly);
        }

        public static void InitializeUseAccessors(Func<IPrincipal> getUserPrincipal, Func<string> getUserIP)
        {
            GetUserPrincipal = getUserPrincipal;
            GetUserIP = getUserIP;
        }

        protected bool ShouldLogExceptions = Config.Get("Log.Record:Exceptions", defaultValue: true);

        protected bool ShouldSkipInsertData = Config.Get("Log.Record:Application:Events.SkipInsertData", defaultValue: true);

        /// <summary>
        /// It's fired just before the event log instance for a SAVE operation is saved in the database.
        /// Handle it to modify the event log instance, add additional data, etc.
        /// </summary>
        public static readonly AsyncEvent<AuditSaveEventArgs> OnRecordingSave = new AsyncEvent<AuditSaveEventArgs>();

        /// <summary>
        /// It's fired just before the event log instance for a delete operation is saved in the database.
        /// Handle it to modify the event log instance, add additional data, etc.
        /// </summary>
        public static readonly AsyncEvent<AuditDeleteEventArgs> OnRecordingDelete =
            new AsyncEvent<AuditDeleteEventArgs>();

        public static void SetApplicationEventType(Type type) => ConcreteApplicationEventType = type;

        /// <summary>
        /// Specifies a factory to instantiate EmailQueueItem objects.
        /// </summary>
        protected virtual IApplicationEvent CreateApplicationEvent()
        {
            if (ConcreteApplicationEventType != null)
                return Activator.CreateInstance(ConcreteApplicationEventType) as IApplicationEvent;

            var possible = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
                {
                    try { return a.GetExportedTypes().Where(t => t.Implements<IApplicationEvent>() && !t.IsInterface).ToList(); }
                    catch { return new List<Type>(); }
                }).ToList();

            if (possible.Count == 0)
                throw new Exception("No type in the currently loaded assemblies implements IApplicationEvent.");

            if (possible.Count > 1)
                throw new Exception($"More than one type in the currently loaded assemblies implement IApplicationEvent:{possible.Select(x => x.FullName).ToString(" and ")}");

            ConcreteApplicationEventType = possible.Single();
            return CreateApplicationEvent();
        }

        protected virtual Task Save(IApplicationEvent eventInfo) => Entity.Database.Save(eventInfo);

        /// <summary>
        /// Gets the IP address of the current user.
        /// </summary>
        public virtual string GetCurrentUserIP()
        {
            if (GetUserIP == null)
                throw new InvalidOperationException("The user info accessors is not set.");

            try
            {
                return GetUserIP();
            }
            catch (Exception err)
            {
                Debug.WriteLine("Cannot get Current user IP:" + err);
                return null;
            }
        }

        /// <summary>
        /// Gets the current user id.
        /// </summary>
        public virtual string GetCurrentUserId(IPrincipal principal)
        {
            try
            {
                return principal?.Identity?.Name;
            }
            catch (Exception err)
            {
                Debug.WriteLine("Cannot get Current user ID:" + err);
                return null;
            }
        }

        string GetCurrentUserId()
        {
            if (GetUserPrincipal == null)
                throw new InvalidOperationException("The user info accessors is not set.");

            return GetCurrentUserId(GetUserPrincipal());
        }

        public virtual async Task RecordSave(IEntity entity, SaveMode saveMode)
        {
            if (!LogEventsAttribute.ShouldLog(entity.GetType())) return;

            var eventInfo = CreateApplicationEvent();

            if (eventInfo == null) return;

            eventInfo.ItemType = entity.GetType().FullName;
            eventInfo.ItemKey = entity.GetId().ToString();
            eventInfo.Event = saveMode.ToString();
            eventInfo.IP = GetCurrentUserIP();
            eventInfo.UserId = GetCurrentUserId();

            if (saveMode == SaveMode.Update)
            {
                var changes = await GetChangesXml(entity);

                if (changes.IsEmpty()) // No changes have happened, ignore recording the action:
                    return;

                eventInfo.Data = changes;
            }
            else
            {
                if (!ShouldSkipInsertData) eventInfo.Data = GetDataXml(entity);
            }

            if (OnRecordingSave.IsHandled())
            {
                var args = new AuditSaveEventArgs
                {
                    SaveMode = saveMode,
                    ApplicationEvent = eventInfo,
                    Entity = entity
                };

                await OnRecordingSave.Raise(args);

                if (args.Cancel) return;
            }

            await Save(eventInfo);

            ProcessContext<UndoContext>.Current.Perform(c => c.Append(eventInfo, entity));
        }

        /// <summary>
        /// Gets the changes applied to the specified object.
        /// Each item in the result will be {PropertyName, { OldValue, NewValue } }.
        /// </summary>
        public virtual IDictionary<string, Tuple<string, string>> GetChanges(IEntity original, IEntity updated)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (updated == null) throw new ArgumentNullException(nameof(updated));
            if (updated.GetType() != original.GetType())
                throw new ArgumentException($"GetChanges() expects two instances of the same type, while {original.GetType().FullName} is not the same as {updated.GetType().FullName}.");

            return Entity.Database.GetProvider(original.GetType()).GetUpdatedValues(original, updated);
        }

        /// <summary>
        /// Gets the changes XML for a specified object. That object should be in its OnSaving event state.
        /// </summary>
        public virtual async Task<string> GetChangesXml(IEntity entityBeingSaved)
        {
            var original = await Entity.Database.Get(entityBeingSaved.GetId(), entityBeingSaved.GetType());
            var changes = Entity.Database.GetProvider(entityBeingSaved.GetType()).GetUpdatedValues(original, entityBeingSaved);

            return ToChangeXml(changes);
        }

        public virtual string GetDataXml(IEntity record)
        {
            var data = GetDataToLog(record);
            return new XElement("Data", data.Select(kv => new XElement(kv.Key, kv.Value))).ToString(SaveOptions.DisableFormatting);
        }

        public virtual async Task RecordDelete(IEntity entity)
        {
            if (!LogEventsAttribute.ShouldLog(entity.GetType())) return;

            var eventInfo = CreateApplicationEvent();
            if (eventInfo == null) return;

            eventInfo.ItemType = entity.GetType().FullName;
            eventInfo.ItemKey = entity.GetId().ToString();
            eventInfo.Event = "Delete";
            eventInfo.IP = GetCurrentUserIP();
            eventInfo.UserId = GetCurrentUserId();

            var changes = Entity.Database.GetProvider(entity.GetType()).GetUpdatedValues(entity, null);
            eventInfo.Data = ToChangeXml(changes);

            if (OnRecordingDelete.IsHandled())
            {
                var args = new AuditDeleteEventArgs { ApplicationEvent = eventInfo, Entity = entity };
                await OnRecordingDelete.Raise(args);
                if (args.Cancel) return;
            }

            await Save(eventInfo);
            ProcessContext<UndoContext>.Current.Perform(c => c.Append(eventInfo, entity));
        }

        public virtual string ToChangeXml(IDictionary<string, Tuple<string, string>> changes)
        {
            if (changes.None()) return null;

            var r = new StringBuilder("<DataChange>");

            r.Append("<old>");

            foreach (var key in changes.Keys)
                r.AppendFormat("<{0}>{1}</{0}>", key, changes[key].Item1.XmlEncode());

            r.Append("</old>");

            r.Append("<new>");
            foreach (var key in changes.Keys)
            {
                var value = changes[key];
                r.AppendFormat("<{0}>{1}</{0}>", key, value.Item2.XmlEncode());
            }

            r.Append("</new>");
            r.Append("</DataChange>");
            return r.ToString();
        }

        /// <summary>
        /// Gets the data of a specified object's properties in a dictionary.
        /// </summary>
        public virtual Dictionary<string, string> GetDataToLog(IEntity entity)
        {
            var result = new Dictionary<string, string>();

            var type = entity.GetType();
            var propertyNames = type.GetProperties().Select(p => p.Name).Distinct().Trim().ToArray();

            Func<string, PropertyInfo> getProperty = name => type.GetProperties()
            .Except(p => p.IsSpecialName)
            .Except(p => p.GetGetMethod().IsStatic)
            .Except(p => p.Name == "ID")
            .Where(p => p.GetSetMethod() != null && p.GetGetMethod().IsPublic)
            .OrderByDescending(x => x.DeclaringType == type)
            .FirstOrDefault(p => p.Name == name);

            var dataProperties = propertyNames.Select(getProperty).ExceptNull()
                                                            .Except(x => CalculatedAttribute.IsCalculated(x))
                                                            .Where(x => LogEventsAttribute.ShouldLog(x))
                                                            .ToArray();

            foreach (var p in dataProperties)
            {
                var propertyType = p.PropertyType;

                string propertyValue;

                try
                {
                    if (propertyType == typeof(IList<Guid>))
                        propertyValue = (p.GetValue(entity) as IList<Guid>).ToString(",");
                    else if (propertyType.IsGenericType)
                        propertyValue = (p.GetValue(entity) as IEnumerable<object>).ToString(", ");
                    else
                        propertyValue = p.GetValue(entity).ToStringOrEmpty();

                    if (propertyValue.IsEmpty()) continue;
                }
                catch
                {
                    // No log needed
                    continue;
                }

                result.Add(p.Name, propertyValue);
            }

            return result;
        }

        /// <summary>
        /// Records the execution result of a scheduled task. 
        /// </summary>
        /// <param name="task">The name of the scheduled task.</param>
        /// <param name="startTime">The time when this task was started.</param>
        public virtual Task RecordScheduledTask(string task, DateTime startTime)
            => RecordScheduledTask(task, startTime, null);

        /// <summary>
        /// Records the execution result of a scheduled task. 
        /// </summary>
        /// <param name="task">The name of the scheduled task.</param>
        /// <param name="startTime">The time when this task was started.</param>
        /// <param name="error">The Exception that occurred during the task execution.</param>
        public virtual async Task RecordScheduledTask(string task, DateTime startTime, Exception error)
        {
            var period = LocalTime.Now - startTime;

            var log = CreateApplicationEvent();
            if (log == null) return;

            log.Event = "Scheduled Task";
            log.ItemType = task;
            log.Data = $"<Execution><Duration unit=\"msec\">{period.TotalMilliseconds}</Duration><Execution>";
            log.ItemKey = error == null ? "Successful" : "Failed";

            await Save(log);

            if (error != null)
            {
                var errorToLog = new Exception($"Error in executing the automated schedule work '{task}'.", error);
                await RecordException(errorToLog);
            }
        }

        /// <summary>
        /// Records the provided exception in the database.
        /// </summary>
        public virtual Task<IApplicationEvent> RecordException(Exception exception) => RecordException(null, exception);

        /// <summary>
        /// Records the provided exception in the database.
        /// </summary>
        public virtual async Task<IApplicationEvent> RecordException(string description, Exception exception)
        {
            if (!ShouldLogExceptions) return null;

            // if (WebTestManager.IsTddExecutionMode()) return null;

            if (exception == null) throw new ArgumentNullException("exception");

            if (exception.GetType().FullName == "NHibernate.ADOException") // Issue in database, can't log:
                return null;

            var data = new StringBuilder();

            if (description.HasValue()) data.AppendLine(description);

            data.AppendLine(exception.Message);
            data.AppendLine(exception.StackTrace);

            if (exception.InnerException != null)
                data.AppendLine(exception.GetBaseException()?.StackTrace);

            for (var inner = exception.InnerException; inner != null; inner = inner.InnerException)
            {
                data.AppendLine("=========================================");
                data.AppendLine("Inner (" + inner.GetType().ToString() + "): " + inner.Message);
            }

            // Now save it:

            var eventInfo = CreateApplicationEvent();
            if (eventInfo == null) return null;

            eventInfo.Event = "Exception";
            eventInfo.Data = data.ToString();
            eventInfo.ItemType = exception.GetType().Name;
            eventInfo.IP = GetCurrentUserIP();
            eventInfo.Date = LocalTime.Now;

            try
            {
                var user = GetCurrentUserId();
                if (user != null) eventInfo.Data += "\r\n-----------------------------\r\nUser:" + user;
            }
            catch { }

            try
            {
                await Save(eventInfo);
                return eventInfo;
            }
            catch (Exception err)
            {
                Debug.WriteLine(err.ToString());
                /* This method should never throw. */
            }

            return null;
        }

        /// <summary>
        /// Logs the specified event as a record in the ApplicationEvents database table.
        /// </summary>
        /// <param name="eventTitle">The event title.</param>
        /// <param name="details">The details of the event.</param>
        /// <param name="owner">The record for which this event is being logged (optional).</param>
        /// <param name="userId">The ID of the user involved in this event (optional). If not specified, the current ASP.NET context user will be used.</param>
        /// <param name="userIp">The IP address of the user involved in this event (optional). If not specified, the IP address of the current Http context (if available) will be used.</param>
        public virtual async Task<IApplicationEvent> Log(string eventTitle, string details, IEntity owner = null, string userId = null, string userIp = null)
        {
            if (eventTitle.IsEmpty())
                throw new ArgumentNullException(nameof(eventTitle));

            if (userId == null)
            {
                try { userId = GetCurrentUserId(); }
                catch (Exception err)
                {
                    Debug.WriteLine("Cannot get current user id:" + err);
                }
            }

            var log = CreateApplicationEvent();
            if (log == null) return null;

            log.Data = details;
            log.Event = eventTitle;
            log.UserId = userId;
            log.IP = GetCurrentUserIP();

            if (owner != null)
            {
                log.ItemType = owner.GetType().FullName;
                dynamic o = owner;
                log.ItemKey = o.ID.ToString();
            }

            await Save(log);

            return log;
        }

        /// <summary>
        /// Loads the item recorded in this event.
        /// </summary>
        public virtual async Task<IEntity> LoadItem(IApplicationEvent applicationEvent)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(applicationEvent.ItemType)).ExceptNull().FirstOrDefault();

            if (type == null)
                throw new Exception("Could not load the type " + applicationEvent.ItemType);

            if (applicationEvent.Event == "Update" || applicationEvent.Event == "Insert")
                return await Entity.Database.Get(applicationEvent.ItemKey.To<Guid>(), type);

            if (applicationEvent.Event == "Delete")
            {
                var result = Activator.CreateInstance(type) as GuidEntity;
                result.ID = applicationEvent.ItemKey.To<Guid>();

                foreach (var p in XElement.Parse(applicationEvent.Data).Elements())
                {
                    var old = p.Value;
                    var property = type.GetProperty(p.Name.LocalName);
                    property.SetValue(result, old.To(property.PropertyType));
                }

                return result;
            }

            throw new NotSupportedException();
        }
    }
}