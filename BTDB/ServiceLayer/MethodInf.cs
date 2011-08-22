using System.Reflection;
using BTDB.KVDBLayer.ReaderWriters;
using BTDB.ODBLayer.FieldHandlerIface;
using BTDB.ODBLayer.FieldHandlerImpl;

namespace BTDB.ServiceLayer
{
    public class MethodInf
    {
        readonly string _name;
        readonly string _ifaceName;
        readonly ParameterInf[] _parameters;
        readonly IFieldHandler _resultFieldHandler;

        public MethodInf(MethodInfo method)
        {
            MethodInfo = method;
            _name = method.Name;
            var methodBase = method.GetBaseDefinition();
            _resultFieldHandler = new SignedFieldHandler();
            if (methodBase != method) _ifaceName = methodBase.DeclaringType.Name;
            var parameterInfos = method.GetParameters();
            _parameters = new ParameterInf[parameterInfos.Length];
            for (int i = 0; i < parameterInfos.Length; i++)
            {
                _parameters[i] = new ParameterInf(parameterInfos[i]);
            }
        }

        public MethodInf(AbstractBufferedReader reader)
        {
            _name = reader.ReadString();
            _ifaceName = reader.ReadString();
            var resultFieldHandlerName = reader.ReadString();
            if (resultFieldHandlerName != null)
            {
                reader.ReadByteArray();
                _resultFieldHandler = new SignedFieldHandler();
            }
            var parameterCount = reader.ReadVUInt32();
            _parameters = new ParameterInf[parameterCount];
            for (int i = 0; i < _parameters.Length; i++)
            {
                _parameters[i] = new ParameterInf(reader);
            }
        }

        public string Name
        {
            get { return _name; }
        }

        public string IfaceName
        {
            get { return _ifaceName; }
        }

        public ParameterInf[] Parameters
        {
            get { return _parameters; }
        }

        public IFieldHandler ResultFieldHandler
        {
            get { return _resultFieldHandler; }
        }

        public MethodInfo MethodInfo { get; set; }

        public void Store(AbstractBufferedWriter writer)
        {
            writer.WriteString(_name);
            writer.WriteString(_ifaceName);
            if (_resultFieldHandler != null)
            {
                writer.WriteString(_resultFieldHandler.Name);
                writer.WriteByteArray(_resultFieldHandler.Configuration);
            }
            else
            {
                writer.WriteString(null);
            }
            writer.WriteVUInt32((uint)_parameters.Length);
            foreach (var parameter in _parameters)
            {
                parameter.Store(writer);
            }
        }
    }
}