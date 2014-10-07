﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.Common;
using System.Data;

namespace SqlWorker
{
    public delegate T GetterDelegate<T>(DbDataReader dr);

    public abstract class ASqlWorker
    {

        protected abstract DbConnection Conn { get; }

        public TimeSpan ReConnectPause { get; set; }
        protected TimeSpan DefaultReconnectPause = new TimeSpan(0, 2, 0);
        protected DateTime? LastDisconnect;

        /// <summary>
        /// constructor
        /// </summary>
        /// <param name="reconnectPause">if null, default will be setted</param>
        public ASqlWorker(TimeSpan? reconnectPause = null)
        {
            ReConnectPause = reconnectPause == null ? DefaultReconnectPause : reconnectPause.Value;
            LastDisconnect = DateTime.Now - reconnectPause;
        }

        virtual public bool OpenConnection(bool ReopenIfNotInTransaction = true)
        {
            if (Conn.State != ConnectionState.Open && ReConnectPause.Ticks > 0)
            {
                if (LastDisconnect == null)
                {
                    LastDisconnect = DateTime.Now;
                    return false;
                }

                if (DateTime.Now - LastDisconnect < ReConnectPause) return false;
            }
            else
            {
                if (TransactionIsOpened || !ReopenIfNotInTransaction) return true;
            }

            LastDisconnect = null;
            if (Conn.State != ConnectionState.Closed) Conn.Close();
            Conn.Open();
            _transactionIsOpened = false;
            return Conn.State == ConnectionState.Open;
        }

        virtual protected String QueryWithParams(String Query, DbParameter[] Params)
        {
            if (Params == null) return Query;

            String newq = Query;
            bool firstParam = true;

            if (newq.IndexOf('@') != -1) firstParam = false;
            foreach (var p in Params)
            {
                if (newq.IndexOf("@" + p.ParameterName) == -1) newq += (firstParam ? " @" : ", @") + p.ParameterName;
                firstParam = false;
            }
            return newq;
        }

        //public ISqlWorker(String connectionString);
        //String QueryWithParams(String Query, DbParameter[] Params);

        protected static void SqlParameterNullWorkaround(DbParameter[] param)
        {
            foreach (var p in param)
                if (p.Value == null) p.Value = DBNull.Value;
        }

        protected static DbParameter[] NotNullParams(DbParameter[] param)
        {
            return (from DbParameter p in param
                    where p.Value != null
                    select p).ToArray();
        }

        abstract protected DbParameter DbParameterConstructor(String paramName, Object paramValue);

        protected DbParameter[] DictionaryToDbParameters(Dictionary<String, Object> input)
        {
            var result = new DbParameter[input.Count];
            int i = 0;
            foreach (var kv in input)
            {
                result[i] = DbParameterConstructor(kv.Key, kv.Value);
                ++i;
            }
            return result;
        }

        protected bool IsNullableParams(params Type[] types)
        {
            bool result = true;
            foreach (var i in types)
                result = result && i.IsGenericType && i.GetGenericTypeDefinition() == typeof(Nullable<>);
            return result;
        }

        #region Transactions
        virtual public void TransactionBegin()
        {
            if (TransactionIsOpened)
            {
                throw new Exception("transaction exists!");
            }
            if (Conn.State != ConnectionState.Open) Conn.Open();
            _transaction = Conn.BeginTransaction();
            _transactionIsOpened = true;
        }

        virtual public void TransactionCommit(bool closeConn = true)
        {
            if (!TransactionIsOpened) throw new Exception("transaction doesnt exist!");
            foreach (var i in Readers) if (i != null) { if (!i.IsClosed) { i.Close(); } i.Dispose(); }
            _transaction.Commit();
            if (closeConn) Conn.Close();
            _transactionIsOpened = false;
        }

        virtual public void TransactionRollback(bool closeConn = true)
        {
            if (!TransactionIsOpened) throw new Exception("transaction doesnt exist!");
            foreach (var i in Readers) if (i != null) { if (!i.IsClosed) { i.Close(); } i.Dispose(); }
            _transaction.Rollback();
            if (closeConn) Conn.Close();
            _transactionIsOpened = false;
        }

        public void DoInTransaction(Action todo, bool closeConn = true)
        {
            TransactionBegin();
            todo();
            TransactionCommit(closeConn);
        }

        private DbTransaction _transaction = null;
        private bool _transactionIsOpened = false;
        public bool TransactionIsOpened { get { return _transactionIsOpened; } }
        #endregion

        protected List<DbDataReader> Readers = new List<DbDataReader>();

        virtual public int ExecuteNonQuery(String Command, Dictionary<String, Object> param)
        { return ExecuteNonQuery(Command, DictionaryToDbParameters(param)); }

        virtual public int ExecuteNonQuery(String Command)
        { return ExecuteNonQuery(Command, new DbParameter[0]); }

        virtual public int ExecuteNonQuery(String Command, DbParameter param)
        { return ExecuteNonQuery(Command, new DbParameter[1] { param }); }

        virtual public int ExecuteNonQuery(String Command, DbParameter[] param)
        {
            SqlParameterNullWorkaround(param);
            DbCommand cmd = Conn.CreateCommand();
            cmd.CommandText = QueryWithParams(Command, param);
            cmd.Parameters.AddRange(param);
            cmd.Transaction = _transaction;
            if (Conn.State != ConnectionState.Open) Conn.Open();
            int result = cmd.ExecuteNonQuery();
            if (!TransactionIsOpened) cmd.Dispose();
            if (!TransactionIsOpened) Conn.Close();
            return result;
        }


        virtual public int InsertValues(String TableName, Dictionary<String, Object> param, bool ReturnIdentity = false)
        { return InsertValues(TableName, DictionaryToDbParameters(param), ReturnIdentity); }

        virtual public int InsertValues(String TableName, DbParameter[] param, bool ReturnIdentity = false)
        {
            SqlParameterNullWorkaround(param);

            String q = "INSERT INTO " + TableName + " (" + param[0].ParameterName;

            for (int i = 1; i < param.Count(); ++i)
                q += ", " + param[i].ParameterName;

            q += ") VALUES (@" + param[0].ParameterName;

            for (int i = 1; i < param.Count(); ++i)
                q += ", @" + param[i].ParameterName;

            q += ");";

            return !ReturnIdentity ?
                ExecuteNonQuery(q, param) :
                Decimal.ToInt32(GetStructFromDB<Decimal>(q + " select SCOPE_IDENTITY()", param, r => { r.Read(); return r.GetDecimal(0); }));
        }


        virtual public int UpdateValues(String TableName, Dictionary<String, Object> param, DbParameter Condition)
        { return UpdateValues(TableName, DictionaryToDbParameters(param), Condition); }

        virtual public int UpdateValues(String TableName, Dictionary<String, Object> param, DbParameter[] Condition)
        { return UpdateValues(TableName, DictionaryToDbParameters(param), Condition); }

        virtual public int UpdateValues(String TableName, DbParameter[] Values, DbParameter Condition)
        { return UpdateValues(TableName, Values, new DbParameter[1] { Condition }); }

        virtual public int UpdateValues(String TableName, DbParameter[] Values, DbParameter[] Condition)
        {
            SqlParameterNullWorkaround(Values);

            String q = "UPDATE " + TableName + " SET " + Values[0].ParameterName + " = @" + Values[0].ParameterName;

            for (int i = 1; i < Values.Count(); ++i)
                q += ", " + Values[i].ParameterName + " = @" + Values[i].ParameterName;

            if (Condition.Count() > 0)
                q += " WHERE " + Condition[0].ParameterName + " = @" + Condition[0].ParameterName;

            for (int i = 1; i < Condition.Count(); ++i)
                q += " AND " + Condition[i].ParameterName + " = @" + Condition[i].ParameterName;

            List<DbParameter> param = new List<DbParameter>(Values);
            param.AddRange(Condition);
            return ExecuteNonQuery(q, param.ToArray());
        }

        virtual public int UpdateValues(String TableName, Dictionary<String, Object> Values, String Condition)
        { return UpdateValues(TableName, DictionaryToDbParameters(Values), Condition); }

        virtual public int UpdateValues(String TableName, DbParameter[] Values, String Condition)
        {
            SqlParameterNullWorkaround(Values);

            String q = "UPDATE " + TableName + " SET " + Values[0].ParameterName + " = @" + Values[0].ParameterName;

            for (int i = 1; i < Values.Count(); ++i)
                q += ", " + Values[i].ParameterName + " = @" + Values[i].ParameterName;

            if (!String.IsNullOrWhiteSpace(Condition))
                q += " WHERE " + Condition;

            return ExecuteNonQuery(q, Values);
        }


        virtual public T GetStructFromDB<T>(String Command, Dictionary<String, Object> param, GetterDelegate<T> todo)
        { return GetStructFromDB<T>(Command, DictionaryToDbParameters(param), todo); }

        virtual public T GetStructFromDB<T>(String Command, GetterDelegate<T> todo)
        { return GetStructFromDB<T>(Command, new DbParameter[0], todo); }

        virtual public T GetStructFromDB<T>(String Command, DbParameter param, GetterDelegate<T> todo)
        { return GetStructFromDB<T>(Command, new DbParameter[1] { param }, todo); }

        virtual public T GetStructFromDB<T>(String Command, DbParameter[] param, GetterDelegate<T> todo)
        {
            SqlParameterNullWorkaround(param);
            DbCommand cmd = Conn.CreateCommand();
            cmd.CommandText = QueryWithParams(Command, param);
            cmd.Parameters.AddRange(param);
            cmd.Transaction = _transaction;
            if (Conn.State != ConnectionState.Open) Conn.Open();
            DbDataReader dr = cmd.ExecuteReader();

            int drid = Readers.Count;
            Readers.Add(dr);

            T result = todo(dr);
            dr.Close();
            dr.Dispose();

            Readers.RemoveAt(drid);

            cmd.Dispose();
            if (!TransactionIsOpened) Conn.Close();

            return result;
        }


        virtual public List<T> GetListFromDBSingleProcessing<T>(String Command, GetterDelegate<T> todo)
        { return GetListFromDBSingleProcessing<T>(Command, new DbParameter[0], todo); }

        virtual public List<T> GetListFromDBSingleProcessing<T>(string Command, Dictionary<String, Object> param, GetterDelegate<T> todo)
        { return GetListFromDBSingleProcessing<T>(Command, DictionaryToDbParameters(param), todo); }

        virtual public List<T> GetListFromDBSingleProcessing<T>(string Command, DbParameter param, GetterDelegate<T> todo)
        { return GetListFromDBSingleProcessing<T>(Command, new DbParameter[1] { param }, todo); }

        /// <summary>
        /// Делегат должен подготавливать один объект из DataReader'а, полностью его создавать и возвращать
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="Command"></param>
        /// <param name="param"></param>
        /// <param name="todo"></param>
        /// <returns></returns>
        virtual public List<T> GetListFromDBSingleProcessing<T>(string Command, DbParameter[] param, GetterDelegate<T> todo)
        {
            return GetStructFromDB<List<T>>(Command, param, delegate(DbDataReader dr)
            {
                List<T> output = new List<T>();
                while (dr.Read())
                {
                    output.Add(todo(dr));
                }
                return output;
            });
        }

        virtual public List<T> GetListFromDB<T>(String procname) where T : new()
        { return GetListFromDB<T>(procname, new DbParameter[0]); }

        virtual public List<T> GetListFromDB<T>(String procname, List<String> Exceptions) where T : new()
        { return GetListFromDB<T>(procname, new DbParameter[0], Exceptions); }

        virtual public List<T> GetListFromDB<T>(String procname, Dictionary<String, Object> param) where T : new()
        { return GetListFromDB<T>(procname, DictionaryToDbParameters(param)); }

        virtual public List<T> GetListFromDB<T>(String procname, Dictionary<String, Object> param, List<String> Exceptions) where T : new()
        { return GetListFromDB<T>(procname, DictionaryToDbParameters(param), Exceptions); }

        virtual public List<T> GetListFromDB<T>(String procname, DbParameter param) where T : new()
        { return GetListFromDB<T>(procname, new DbParameter[1] { param }); }

        virtual public List<T> GetListFromDB<T>(String procname, DbParameter[] param) where T : new()
        {
            return GetStructFromDB<List<T>>(procname, param, delegate(DbDataReader dr)
            {
                List<T> result = new List<T>();
                while (dr.Read())
                {
                    result.Add(DataReaderToObj<T>(dr));
                }
                return result;
            });
        }

        virtual public List<T> GetListFromDB<T>(String procname, DbParameter param, List<String> Exceptions) where T : new()
        { return GetListFromDB<T>(procname, new DbParameter[1] { param }, Exceptions); }

        virtual public List<T> GetListFromDB<T>(String procname, DbParameter[] param, List<String> Exceptions) where T : new()
        {
            return GetStructFromDB<List<T>>(procname, param, delegate(DbDataReader dr)
            {
                List<T> result = new List<T>();
                while (dr.Read())
                {
                    result.Add(DataReaderToObj<T>(dr, Exceptions));
                }
                return result;
            });
        }


        virtual public List<T> GetScalarsListFromDB<T>(String procname)
        { return GetScalarsListFromDB<T>(procname, new DbParameter[0]); }

        virtual public List<T> GetScalarsListFromDB<T>(String procname, DbParameter param)
        { return GetScalarsListFromDB<T>(procname, new DbParameter[1] { param }); }

        virtual public List<T> GetScalarsListFromDB<T>(String procname, Dictionary<String, Object> param)
        { return GetScalarsListFromDB<T>(procname, DictionaryToDbParameters(param)); }

        virtual public List<T> GetScalarsListFromDB<T>(String procname, DbParameter[] param)
        {
            bool IncludingNulls = IsNullableParams(typeof(T));
            if (IncludingNulls)
                return GetListFromDBSingleProcessing<T>(
                    procname,
                    param,
                    (DbDataReader dr) => dr[0] == DBNull.Value ? (T)(Object)null : (T)dr[0]
                    );
            else return GetStructFromDB<List<T>>(procname, param, (dr) =>
            {
                List<T> result = new List<T>();
                while (dr.Read())
                    if (dr[0] != DBNull.Value) result.Add((T)dr[0]);
                return result;
            });
        }


        virtual public List<Tuple<T1, T2>> GetTupleFromDB<T1, T2>(String query)
        { return GetTupleFromDB<T1, T2>(query, new DbParameter[0]); }

        virtual public List<Tuple<T1, T2>> GetTupleFromDB<T1, T2>(String query, DbParameter param)
        { return GetTupleFromDB<T1, T2>(query, new DbParameter[1] { param }); }

        virtual public List<Tuple<T1, T2>> GetTupleFromDB<T1, T2>(String query, Dictionary<String, Object> param)
        { return GetTupleFromDB<T1, T2>(query, DictionaryToDbParameters(param)); }

        virtual public List<Tuple<T1, T2>> GetTupleFromDB<T1, T2>(String query, DbParameter[] param)
        {
            bool IncludingNulls = IsNullableParams(typeof(T1), typeof(T2));
            if (IncludingNulls)
            return GetListFromDBSingleProcessing<Tuple<T1, T2>>(query, param,
                (dr) =>
                {
                    return new Tuple<T1,T2>((T1)dr[0], (T2)dr[1]);
                });
            return GetStructFromDB<List<Tuple<T1, T2>>>(query, param,
                (dr) =>
                {
                    var result = new List<Tuple<T1, T2>>();
                    while (dr.Read())
                    {
                        var x0 = dr[0];
                        var x1 = dr[1];
                        if (x0 != DBNull.Value && x1 != DBNull.Value)
                            result.Add(new Tuple<T1, T2>((T1)x0, (T2)x1));
                    }
                    return result;
                });
        }

        virtual public T DataReaderToObj<T>(DbDataReader dr, List<String> Errors) where T : new()
        {
            T result = new T();
            foreach (System.ComponentModel.PropertyDescriptor i in System.ComponentModel.TypeDescriptor.GetProperties(result))
            {
                try { if (dr[i.Name] != DBNull.Value) i.SetValue(result, dr[i.Name]); }
                catch (Exception e) { Errors.Add(e.ToString()); }
            }

            return result;
        }

        virtual public T DataReaderToObj<T>(DbDataReader dr) where T : new()
        {
            T result = new T();
            foreach (System.ComponentModel.PropertyDescriptor i in System.ComponentModel.TypeDescriptor.GetProperties(result))
            {
                if (dr[i.Name] != DBNull.Value) i.SetValue(result, dr[i.Name]);
            }

            return result;
        }
    }
}
