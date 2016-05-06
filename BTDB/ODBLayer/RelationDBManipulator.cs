using System;
using System.Collections.Generic;
using BTDB.Buffer;
using BTDB.KVDBLayer;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    public class RelationDBManipulator<T>
    {
        readonly IInternalObjectDBTransaction _transaction;
        readonly RelationInfo _relationInfo;

        public RelationDBManipulator(IObjectDBTransaction transation, RelationInfo relationInfo)
        {
            _transaction = (IInternalObjectDBTransaction)transation;
            _relationInfo = relationInfo;
        }

        ByteBuffer ValueBytes(T obj)
        {
            var valueWriter = new ByteBufferWriter();
            valueWriter.WriteVUInt32(_relationInfo.ClientTypeVersion);
            _relationInfo.ValueSaver(_transaction, valueWriter, obj);
            return valueWriter.Data;
        }

        ByteBuffer KeyBytes(T obj)
        {
            var keyWriter = new ByteBufferWriter();
            keyWriter.WriteVUInt32(_relationInfo.Id);
            _relationInfo.PrimaryKeysSaver(_transaction, keyWriter, obj, this);  //this for relation interface which is same with manipulator
            return keyWriter.Data;
        }

        void StartWorkingWithPK()
        {
            _transaction.TransactionProtector.Start();
            _transaction.KeyValueDBTransaction.SetKeyPrefix(ObjectDB.AllRelationsPKPrefix);
        }

        bool HasSecondaryIndexes => _relationInfo.ClientRelationVersionInfo.HasSecondaryIndexes;

        public void Insert(T obj)
        {
            var keyBytes = KeyBytes(obj);
            var valueBytes = ValueBytes(obj);

            StartWorkingWithPK();

            if (_transaction.KeyValueDBTransaction.Find(keyBytes) == FindResult.Exact)
                throw new BTDBException("Trying to insert duplicate key.");  //todo write key in message
            _transaction.KeyValueDBTransaction.CreateOrUpdateKeyValue(keyBytes, valueBytes);

            if (HasSecondaryIndexes)
            {
                var error = AddIntoSecondaryIndexes(obj);
                if (error != null)
                {
                    StartWorkingWithPK();
                    _transaction.KeyValueDBTransaction.Find(keyBytes);
                    _transaction.KeyValueDBTransaction.EraseCurrent();
                    throw new BTDBException(error);
                }
            }
        }

        //todo check whether dictionaries are not replaced in upsert - leaks
        public bool Upsert(T obj)
        {
            var keyBytes = KeyBytes(obj);
            var valueBytes = ValueBytes(obj);

            if (HasSecondaryIndexes)
            {
                var oldValue = FindByIdOrDefault(keyBytes, false);
                var error = oldValue != null
                    ? UpdateSecondaryIndexes(obj, oldValue)
                    : AddIntoSecondaryIndexes(obj);
                if (error != null)
                    throw new BTDBException(error);
            }
            StartWorkingWithPK();
            return _transaction.KeyValueDBTransaction.CreateOrUpdateKeyValue(keyBytes, valueBytes);
        }

        //todo check whether dictionaries are not replaced in update - leaks
        public void Update(T obj)
        {
            var keyBytes = KeyBytes(obj);
            var valueBytes = ValueBytes(obj);

            if (HasSecondaryIndexes)
            {
                var oldValue = FindByIdOrDefault(keyBytes, true);
                var error = UpdateSecondaryIndexes(obj, oldValue);
                if (error != null)
                    throw new BTDBException(error);
            }

            StartWorkingWithPK();

            if (_transaction.KeyValueDBTransaction.Find(keyBytes) != FindResult.Exact)
                throw new BTDBException("Not found record to update."); //todo write key in message
            _transaction.KeyValueDBTransaction.CreateOrUpdateKeyValue(keyBytes, valueBytes);
        }

        public bool RemoveById(ByteBuffer keyBytes, bool throwWhenNotFound)
        {
            if (HasSecondaryIndexes)
            {
                var obj = FindByIdOrDefault(keyBytes, throwWhenNotFound);
                if (obj != null)
                    RemoveSecondaryIndexes(obj);
            }

            StartWorkingWithPK();
            if (_transaction.KeyValueDBTransaction.Find(keyBytes) != FindResult.Exact)
            {
                if (throwWhenNotFound)
                    throw new BTDBException("Not found record to delete.");
                return false;
            }
            if (_relationInfo.NeedsFreeContent)
            {
                long current = _transaction.TransactionProtector.ProtectionCounter;
                var valueBytes = _transaction.KeyValueDBTransaction.GetValue();
                _relationInfo.FreeContent(_transaction, valueBytes);
                if (_transaction.TransactionProtector.WasInterupted(current))
                {
                    StartWorkingWithPK();
                    _transaction.KeyValueDBTransaction.Find(keyBytes);
                }
            }
            _transaction.KeyValueDBTransaction.EraseCurrent();
            return true;
        }

        public IEnumerator<T> GetEnumerator()
        {
            var keyWriter = new ByteBufferWriter();
            keyWriter.WriteByteArrayRaw(ObjectDB.AllRelationsPKPrefix);
            keyWriter.WriteVUInt32(_relationInfo.Id);

            return new RelationEnumerator<T>(_transaction, _relationInfo, keyWriter.Data.ToAsyncSafe());
        }

        public T FindByIdOrDefault(ByteBuffer keyBytes, bool throwWhenNotFound)
        {
            StartWorkingWithPK();
            if (_transaction.KeyValueDBTransaction.Find(keyBytes) != FindResult.Exact)
            {
                if (throwWhenNotFound)
                    throw new BTDBException("Not found.");
                return default(T);
            }
            var valueBytes = _transaction.KeyValueDBTransaction.GetValue();
            return (T)_relationInfo.CreateInstance(_transaction, keyBytes, valueBytes);
        }

        class RelationSKEnumerator<T> : RelationEnumerator<T>
        {
            readonly uint _secondaryKeyIndex;
            readonly RelationDBManipulator<T> _manipulator;

            public RelationSKEnumerator(IInternalObjectDBTransaction tr, RelationInfo relationInfo, ByteBuffer keyBytes,
                uint secondaryKeyIndex, RelationDBManipulator<T> manipulator)
                : base(tr, relationInfo, keyBytes)
            {
                _secondaryKeyIndex = secondaryKeyIndex;
                _manipulator = manipulator;
            }

            protected override T CreateInstance(ByteBuffer keyBytes, ByteBuffer valueBytes)
            {
                return _manipulator.CreateInstanceFromSK(_secondaryKeyIndex, keyBytes, valueBytes);
            }
        }

        T CreateInstanceFromSK(uint secondaryKeyIndex, ByteBuffer keyBytes, ByteBuffer valueBytes)
        {
            var pkWriter = new ByteBufferWriter();
            pkWriter.WriteVUInt32(_relationInfo.Id);
            _relationInfo.GetSKKeyValuetoPKMerger(secondaryKeyIndex)(keyBytes.ToByteArray(),
                                                                     valueBytes.ToByteArray(), pkWriter);
            return FindByIdOrDefault(pkWriter.Data, true);
        }

        public IEnumerator<T> FindBySecondaryKey(uint secondaryKeyIndex, ByteBuffer secKeyBytes)
        {
            var keyWriter = new ByteBufferWriter();
            keyWriter.WriteBlock(secKeyBytes);

            return new RelationSKEnumerator<T>(_transaction, _relationInfo, keyWriter.Data.ToAsyncSafe(),
                secondaryKeyIndex, this);
        }

        //secKeyBytes contains already AllRelationsSKPrefix
        public T FindBySecondaryKeyOrDefault(uint secondaryKeyIndex, ByteBuffer secKeyBytes, bool throwWhenNotFound)
        {
            _transaction.KeyValueDBTransaction.SetKeyPrefix(secKeyBytes);
            if (!_transaction.KeyValueDBTransaction.FindFirstKey())
            {
                if (throwWhenNotFound)
                    throw new BTDBException("Not found.");
                return default(T);
            }
            var keyBytes = _transaction.KeyValueDBTransaction.GetKey();
            var valueBytes = _transaction.KeyValueDBTransaction.GetValue();

            if (_transaction.KeyValueDBTransaction.FindNextKey())
                throw new BTDBException("Ambiguous result.");

            return CreateInstanceFromSK(secondaryKeyIndex, keyBytes, valueBytes);
        }

        //SK manipulations
        void StartWorkingWithSK()
        {
            _transaction.TransactionProtector.Start();
            _transaction.KeyValueDBTransaction.SetKeyPrefix(ObjectDB.AllRelationsSKPrefix);
        }

        ByteBuffer WriteSecodaryKeyKey(uint secondaryKeyIndex, SecondaryKeyInfo keyInfo, T obj)
        {
            var keyWriter = new ByteBufferWriter();
            var keySaver = _relationInfo.GetSecondaryKeysKeySaver(secondaryKeyIndex, keyInfo.Name);
            keyWriter.WriteVUInt32(_relationInfo.Id);
            keyWriter.WriteVUInt32(secondaryKeyIndex); //secondary key index
            keySaver(_transaction, keyWriter, obj, this); //secondary key
            return keyWriter.Data;
        }

        ByteBuffer WriteSecondaryKeyValue(uint secondaryKeyIndex, SecondaryKeyInfo keyInfo, T obj)
        {
            var valueWriter = new ByteBufferWriter();
            var valueSaver = _relationInfo.GetSecondaryKeysValueSaver(secondaryKeyIndex, keyInfo.Name);
            valueSaver(_transaction, valueWriter, obj, this);
            return valueWriter.Data;
        }

        string AddIntoSecondaryIndexes(T obj)
        {
            StartWorkingWithSK();

            foreach (var sk in _relationInfo.ClientRelationVersionInfo.SecondaryKeys)
            {
                var keyBytes = WriteSecodaryKeyKey(sk.Key, sk.Value, obj);
                var valueBytes = WriteSecondaryKeyValue(sk.Key, sk.Value, obj);

                if (_transaction.KeyValueDBTransaction.Find(keyBytes) == FindResult.Exact)
                {
                    //reverting previous sk inserts
                    foreach (var rsk in _relationInfo.ClientRelationVersionInfo.SecondaryKeys)
                    {
                        if (rsk.Key == sk.Key)
                            break;
                        var kb = WriteSecodaryKeyKey(rsk.Key, rsk.Value, obj);
                        if (_transaction.KeyValueDBTransaction.Find(kb) != FindResult.Exact)
                            throw new BTDBException("Error when reverting failed secondary indexes.");
                        _transaction.KeyValueDBTransaction.EraseCurrent();
                    }
                    return "Try to insert duplicate secondary key";
                }
                _transaction.KeyValueDBTransaction.CreateOrUpdateKeyValue(keyBytes, valueBytes);
            }
            return null;
        }

        string UpdateSecondaryIndexes(T newValue, T oldValue)
        {
            throw new NotImplementedException();
        }

        void RemoveSecondaryIndexes(T obj)
        {
            StartWorkingWithSK();

            foreach (var sk in _relationInfo.ClientRelationVersionInfo.SecondaryKeys)
            {
                var keyBytes = WriteSecodaryKeyKey(sk.Key, sk.Value, obj);
                if (_transaction.KeyValueDBTransaction.Find(keyBytes) == FindResult.Exact)
                    _transaction.KeyValueDBTransaction.EraseCurrent();
            }

        }
    }
}