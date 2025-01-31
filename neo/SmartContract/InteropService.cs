using Neo.Cryptography;
using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract.Manifest;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Array = Neo.VM.Types.Array;
using Boolean = Neo.VM.Types.Boolean;

namespace Neo.SmartContract
{
    public static partial class InteropService
    {
        public const long GasPerByte = 100000;
        public const int MaxStorageKeySize = 64;
        public const int MaxStorageValueSize = ushort.MaxValue;
        public const int MaxNotificationSize = 1024;

        private static readonly Dictionary<uint, InteropDescriptor> methods = new Dictionary<uint, InteropDescriptor>();

        public static readonly uint System_ExecutionEngine_GetScriptContainer = Register("System.ExecutionEngine.GetScriptContainer", ExecutionEngine_GetScriptContainer, 0_00000250, TriggerType.All);
        public static readonly uint System_ExecutionEngine_GetExecutingScriptHash = Register("System.ExecutionEngine.GetExecutingScriptHash", ExecutionEngine_GetExecutingScriptHash, 0_00000400, TriggerType.All);
        public static readonly uint System_ExecutionEngine_GetCallingScriptHash = Register("System.ExecutionEngine.GetCallingScriptHash", ExecutionEngine_GetCallingScriptHash, 0_00000400, TriggerType.All);
        public static readonly uint System_ExecutionEngine_GetEntryScriptHash = Register("System.ExecutionEngine.GetEntryScriptHash", ExecutionEngine_GetEntryScriptHash, 0_00000400, TriggerType.All);
        public static readonly uint System_Runtime_Platform = Register("System.Runtime.Platform", Runtime_Platform, 0_00000250, TriggerType.All);
        public static readonly uint System_Runtime_GetTrigger = Register("System.Runtime.GetTrigger", Runtime_GetTrigger, 0_00000250, TriggerType.All);
        public static readonly uint System_Runtime_CheckWitness = Register("System.Runtime.CheckWitness", Runtime_CheckWitness, 0_00030000, TriggerType.All);
        public static readonly uint System_Runtime_Notify = Register("System.Runtime.Notify", Runtime_Notify, 0_01000000, TriggerType.All);
        public static readonly uint System_Runtime_Log = Register("System.Runtime.Log", Runtime_Log, 0_01000000, TriggerType.All);
        public static readonly uint System_Runtime_GetTime = Register("System.Runtime.GetTime", Runtime_GetTime, 0_00000250, TriggerType.Application);
        public static readonly uint System_Runtime_Serialize = Register("System.Runtime.Serialize", Runtime_Serialize, 0_00100000, TriggerType.All);
        public static readonly uint System_Runtime_Deserialize = Register("System.Runtime.Deserialize", Runtime_Deserialize, 0_00500000, TriggerType.All);
        public static readonly uint System_Runtime_GetInvocationCounter = Register("System.Runtime.GetInvocationCounter", Runtime_GetInvocationCounter, 0_00000400, TriggerType.All);
        public static readonly uint System_Runtime_GetNotifications = Register("System.Runtime.GetNotifications", Runtime_GetNotifications, 0_00010000, TriggerType.All);
        public static readonly uint System_Crypto_Verify = Register("System.Crypto.Verify", Crypto_Verify, 0_01000000, TriggerType.All);
        public static readonly uint System_Blockchain_GetHeight = Register("System.Blockchain.GetHeight", Blockchain_GetHeight, 0_00000400, TriggerType.Application);
        public static readonly uint System_Blockchain_GetBlock = Register("System.Blockchain.GetBlock", Blockchain_GetBlock, 0_02500000, TriggerType.Application);
        public static readonly uint System_Blockchain_GetTransaction = Register("System.Blockchain.GetTransaction", Blockchain_GetTransaction, 0_01000000, TriggerType.Application);
        public static readonly uint System_Blockchain_GetTransactionHeight = Register("System.Blockchain.GetTransactionHeight", Blockchain_GetTransactionHeight, 0_01000000, TriggerType.Application);
        public static readonly uint System_Blockchain_GetTransactionFromBlock = Register("System.Blockchain.GetTransactionFromBlock", Blockchain_GetTransactionFromBlock, 0_01000000, TriggerType.Application);
        public static readonly uint System_Blockchain_GetContract = Register("System.Blockchain.GetContract", Blockchain_GetContract, 0_01000000, TriggerType.Application);
        public static readonly uint System_Contract_Call = Register("System.Contract.Call", Contract_Call, 0_01000000, TriggerType.System | TriggerType.Application);
        public static readonly uint System_Contract_Destroy = Register("System.Contract.Destroy", Contract_Destroy, 0_01000000, TriggerType.Application);
        public static readonly uint System_Storage_GetContext = Register("System.Storage.GetContext", Storage_GetContext, 0_00000400, TriggerType.Application);
        public static readonly uint System_Storage_GetReadOnlyContext = Register("System.Storage.GetReadOnlyContext", Storage_GetReadOnlyContext, 0_00000400, TriggerType.Application);
        public static readonly uint System_Storage_Get = Register("System.Storage.Get", Storage_Get, 0_01000000, TriggerType.Application);
        public static readonly uint System_Storage_Put = Register("System.Storage.Put", Storage_Put, GetStoragePrice, TriggerType.Application);
        public static readonly uint System_Storage_PutEx = Register("System.Storage.PutEx", Storage_PutEx, GetStoragePrice, TriggerType.Application);
        public static readonly uint System_Storage_Delete = Register("System.Storage.Delete", Storage_Delete, 0_01000000, TriggerType.Application);
        public static readonly uint System_StorageContext_AsReadOnly = Register("System.StorageContext.AsReadOnly", StorageContext_AsReadOnly, 0_00000400, TriggerType.Application);

        private static bool CheckItemForNotification(StackItem state)
        {
            int size = 0;
            List<StackItem> items_checked = new List<StackItem>();
            Queue<StackItem> items_unchecked = new Queue<StackItem>();
            while (true)
            {
                switch (state)
                {
                    case Struct array:
                        foreach (StackItem item in array)
                            items_unchecked.Enqueue(item);
                        break;
                    case Array array:
                        if (items_checked.All(p => !ReferenceEquals(p, array)))
                        {
                            items_checked.Add(array);
                            foreach (StackItem item in array)
                                items_unchecked.Enqueue(item);
                        }
                        break;
                    case Boolean _:
                    case ByteArray _:
                    case Integer _:
                        size += state.GetByteLength();
                        break;
                    case Null _:
                        break;
                    case InteropInterface _:
                        return false;
                    case Map map:
                        if (items_checked.All(p => !ReferenceEquals(p, map)))
                        {
                            items_checked.Add(map);
                            foreach (var pair in map)
                            {
                                size += pair.Key.GetByteLength();
                                items_unchecked.Enqueue(pair.Value);
                            }
                        }
                        break;
                }
                if (size > MaxNotificationSize) return false;
                if (items_unchecked.Count == 0) return true;
                state = items_unchecked.Dequeue();
            }
        }

        private static bool CheckStorageContext(ApplicationEngine engine, StorageContext context)
        {
            ContractState contract = engine.Snapshot.Contracts.TryGet(context.ScriptHash);
            if (contract == null) return false;
            if (!contract.HasStorage) return false;
            return true;
        }

        public static long GetPrice(uint hash, RandomAccessStack<StackItem> stack)
        {
            return methods[hash].GetPrice(stack);
        }

        public static Dictionary<uint, string> SupportedMethods()
        {
            return methods.ToDictionary(p => p.Key, p => p.Value.Method);
        }

        private static long GetStoragePrice(RandomAccessStack<StackItem> stack)
        {
            return (stack.Peek(1).GetByteLength() + stack.Peek(2).GetByteLength()) * GasPerByte;
        }

        internal static bool Invoke(ApplicationEngine engine, uint method)
        {
            if (!methods.TryGetValue(method, out InteropDescriptor descriptor))
                return false;
            if (!descriptor.AllowedTriggers.HasFlag(engine.Trigger))
                return false;
            return descriptor.Handler(engine);
        }

        private static uint Register(string method, Func<ApplicationEngine, bool> handler, long price, TriggerType allowedTriggers)
        {
            InteropDescriptor descriptor = new InteropDescriptor(method, handler, price, allowedTriggers);
            methods.Add(descriptor.Hash, descriptor);
            return descriptor.Hash;
        }

        private static uint Register(string method, Func<ApplicationEngine, bool> handler, Func<RandomAccessStack<StackItem>, long> priceCalculator, TriggerType allowedTriggers)
        {
            InteropDescriptor descriptor = new InteropDescriptor(method, handler, priceCalculator, allowedTriggers);
            methods.Add(descriptor.Hash, descriptor);
            return descriptor.Hash;
        }

        private static bool ExecutionEngine_GetScriptContainer(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(
                engine.ScriptContainer is IInteroperable value ? value.ToStackItem() :
                StackItem.FromInterface(engine.ScriptContainer));
            return true;
        }

        private static bool ExecutionEngine_GetExecutingScriptHash(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.CurrentScriptHash.ToArray());
            return true;
        }

        private static bool ExecutionEngine_GetCallingScriptHash(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.CallingScriptHash?.ToArray() ?? StackItem.Null);
            return true;
        }

        private static bool ExecutionEngine_GetEntryScriptHash(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.EntryScriptHash.ToArray());
            return true;
        }

        private static bool Runtime_Platform(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(Encoding.ASCII.GetBytes("NEO"));
            return true;
        }

        private static bool Runtime_GetTrigger(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push((int)engine.Trigger);
            return true;
        }

        internal static bool CheckWitness(ApplicationEngine engine, UInt160 hash)
        {
            if (engine.ScriptContainer is Transaction tx)
            {
                Cosigner usage = tx.Cosigners.FirstOrDefault(p => p.Account.Equals(hash));
                if (usage is null) return false;
                if (usage.Scopes == WitnessScope.Global) return true;
                if (usage.Scopes.HasFlag(WitnessScope.CalledByEntry))
                {
                    if (engine.CallingScriptHash == engine.EntryScriptHash)
                        return true;
                }
                if (usage.Scopes.HasFlag(WitnessScope.CustomContracts))
                {
                    if (usage.AllowedContracts.Contains(engine.CurrentScriptHash))
                        return true;
                }
                if (usage.Scopes.HasFlag(WitnessScope.CustomGroups))
                {
                    var contract = engine.Snapshot.Contracts[engine.CallingScriptHash];
                    // check if current group is the required one
                    if (contract.Manifest.Groups.Select(p => p.PubKey).Intersect(usage.AllowedGroups).Any())
                        return true;
                }
                return false;
            }

            // only for non-Transaction types (Block, etc)

            var hashes_for_verifying = engine.ScriptContainer.GetScriptHashesForVerifying(engine.Snapshot);
            return hashes_for_verifying.Contains(hash);
        }

        private static bool CheckWitness(ApplicationEngine engine, ECPoint pubkey)
        {
            return CheckWitness(engine, Contract.CreateSignatureRedeemScript(pubkey).ToScriptHash());
        }

        private static bool Runtime_CheckWitness(ApplicationEngine engine)
        {
            byte[] hashOrPubkey = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            bool result;
            if (hashOrPubkey.Length == 20)
                result = CheckWitness(engine, new UInt160(hashOrPubkey));
            else if (hashOrPubkey.Length == 33)
                result = CheckWitness(engine, ECPoint.DecodePoint(hashOrPubkey, ECCurve.Secp256r1));
            else
                return false;
            engine.CurrentContext.EvaluationStack.Push(result);
            return true;
        }

        private static bool Runtime_Notify(ApplicationEngine engine)
        {
            StackItem state = engine.CurrentContext.EvaluationStack.Pop();
            if (!CheckItemForNotification(state)) return false;
            engine.SendNotification(engine.CurrentScriptHash, state);
            return true;
        }

        private static bool Runtime_Log(ApplicationEngine engine)
        {
            byte[] state = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            if (state.Length > MaxNotificationSize) return false;
            string message = Encoding.UTF8.GetString(state);
            engine.SendLog(engine.CurrentScriptHash, message);
            return true;
        }

        private static bool Runtime_GetTime(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.Snapshot.PersistingBlock.Timestamp);
            return true;
        }

        private static bool Runtime_Serialize(ApplicationEngine engine)
        {
            byte[] serialized;
            try
            {
                serialized = engine.CurrentContext.EvaluationStack.Pop().Serialize();
            }
            catch (NotSupportedException)
            {
                return false;
            }
            if (serialized.Length > engine.MaxItemSize)
                return false;
            engine.CurrentContext.EvaluationStack.Push(serialized);
            return true;
        }

        private static bool Runtime_GetNotifications(ApplicationEngine engine)
        {
            StackItem item = engine.CurrentContext.EvaluationStack.Pop();

            IEnumerable<NotifyEventArgs> notifications = engine.Notifications;
            if (!item.IsNull) // must filter by scriptHash
            {
                var hash = new UInt160(item.GetByteArray());
                notifications = notifications.Where(p => p.ScriptHash == hash);
            }

            if (!engine.CheckArraySize(notifications.Count())) return false;
            engine.CurrentContext.EvaluationStack.Push(notifications.Select(u => new VM.Types.Array(new StackItem[] { u.ScriptHash.ToArray(), u.State })).ToArray());
            return true;
        }

        private static bool Runtime_GetInvocationCounter(ApplicationEngine engine)
        {
            if (!engine.InvocationCounter.TryGetValue(engine.CurrentScriptHash, out var counter))
            {
                return false;
            }

            engine.CurrentContext.EvaluationStack.Push(counter);
            return true;
        }

        private static bool Runtime_Deserialize(ApplicationEngine engine)
        {
            StackItem item;
            try
            {
                item = engine.CurrentContext.EvaluationStack.Pop().GetByteArray().DeserializeStackItem(engine.MaxArraySize, engine.MaxItemSize);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            engine.CurrentContext.EvaluationStack.Push(item);
            return true;
        }

        private static bool Crypto_Verify(ApplicationEngine engine)
        {
            StackItem item0 = engine.CurrentContext.EvaluationStack.Pop();
            byte[] message;
            if (item0 is InteropInterface _interface)
                message = _interface.GetInterface<IVerifiable>().GetHashData();
            else
                message = item0.GetByteArray();
            byte[] pubkey = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            if (pubkey[0] != 2 && pubkey[0] != 3 && pubkey[0] != 4)
                return false;
            byte[] signature = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            bool result = Crypto.Default.VerifySignature(message, signature, pubkey);
            engine.CurrentContext.EvaluationStack.Push(result);
            return true;
        }

        private static bool Blockchain_GetHeight(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.Snapshot.Height);
            return true;
        }

        private static bool Blockchain_GetBlock(ApplicationEngine engine)
        {
            byte[] data = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            UInt256 hash;
            if (data.Length <= 5)
                hash = Blockchain.Singleton.GetBlockHash((uint)new BigInteger(data));
            else if (data.Length == 32)
                hash = new UInt256(data);
            else
                return false;

            Block block = hash != null ? engine.Snapshot.GetBlock(hash) : null;
            if (block == null)
                engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            else
                engine.CurrentContext.EvaluationStack.Push(block.ToStackItem());
            return true;
        }

        private static bool Blockchain_GetTransaction(ApplicationEngine engine)
        {
            byte[] hash = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            Transaction tx = engine.Snapshot.GetTransaction(new UInt256(hash));
            if (tx == null)
                engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            else
                engine.CurrentContext.EvaluationStack.Push(tx.ToStackItem());
            return true;
        }

        private static bool Blockchain_GetTransactionHeight(ApplicationEngine engine)
        {
            byte[] hash = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            var tx = engine.Snapshot.Transactions.TryGet(new UInt256(hash));
            engine.CurrentContext.EvaluationStack.Push(tx != null ? new BigInteger(tx.BlockIndex) : BigInteger.MinusOne);
            return true;
        }

        private static bool Blockchain_GetTransactionFromBlock(ApplicationEngine engine)
        {
            byte[] data = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            UInt256 hash;
            if (data.Length <= 5)
                hash = Blockchain.Singleton.GetBlockHash((uint)new BigInteger(data));
            else if (data.Length == 32)
                hash = new UInt256(data);
            else
                return false;

            TrimmedBlock block = hash != null ? engine.Snapshot.Blocks.TryGet(hash) : null;
            if (block == null)
            {
                engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            }
            else
            {
                int index = (int)engine.CurrentContext.EvaluationStack.Pop().GetBigInteger();
                if (index < 0 || index >= block.Hashes.Length - 1) return false;

                Transaction tx = engine.Snapshot.GetTransaction(block.Hashes[index + 1]);
                if (tx == null)
                    engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
                else
                    engine.CurrentContext.EvaluationStack.Push(tx.ToStackItem());
            }
            return true;
        }

        private static bool Blockchain_GetContract(ApplicationEngine engine)
        {
            UInt160 hash = new UInt160(engine.CurrentContext.EvaluationStack.Pop().GetByteArray());
            ContractState contract = engine.Snapshot.Contracts.TryGet(hash);
            if (contract == null)
                engine.CurrentContext.EvaluationStack.Push(StackItem.Null);
            else
                engine.CurrentContext.EvaluationStack.Push(contract.ToStackItem());
            return true;
        }

        private static bool Storage_GetContext(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(new StorageContext
            {
                ScriptHash = engine.CurrentScriptHash,
                IsReadOnly = false
            }));
            return true;
        }

        private static bool Storage_GetReadOnlyContext(ApplicationEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(new StorageContext
            {
                ScriptHash = engine.CurrentScriptHash,
                IsReadOnly = true
            }));
            return true;
        }

        private static bool Storage_Get(ApplicationEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                StorageContext context = _interface.GetInterface<StorageContext>();
                if (!CheckStorageContext(engine, context)) return false;
                byte[] key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
                StorageItem item = engine.Snapshot.Storages.TryGet(new StorageKey
                {
                    ScriptHash = context.ScriptHash,
                    Key = key
                });
                engine.CurrentContext.EvaluationStack.Push(item?.Value ?? StackItem.Null);
                return true;
            }
            return false;
        }

        private static bool StorageContext_AsReadOnly(ApplicationEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                StorageContext context = _interface.GetInterface<StorageContext>();
                if (!context.IsReadOnly)
                    context = new StorageContext
                    {
                        ScriptHash = context.ScriptHash,
                        IsReadOnly = true
                    };
                engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(context));
                return true;
            }
            return false;
        }

        private static bool Contract_Call(ApplicationEngine engine)
        {
            StackItem contractHash = engine.CurrentContext.EvaluationStack.Pop();

            ContractState contract = engine.Snapshot.Contracts.TryGet(new UInt160(contractHash.GetByteArray()));
            if (contract is null) return false;

            StackItem method = engine.CurrentContext.EvaluationStack.Pop();
            StackItem args = engine.CurrentContext.EvaluationStack.Pop();
            ContractManifest currentManifest = engine.Snapshot.Contracts.TryGet(engine.CurrentScriptHash)?.Manifest;

            if (currentManifest != null && !currentManifest.CanCall(contract.Manifest, method.GetString()))
                return false;

            if (engine.InvocationCounter.TryGetValue(contract.ScriptHash, out var counter))
            {
                engine.InvocationCounter[contract.ScriptHash] = counter + 1;
            }
            else
            {
                engine.InvocationCounter[contract.ScriptHash] = 1;
            }

            ExecutionContext context_new = engine.LoadScript(contract.Script, 1);
            context_new.EvaluationStack.Push(args);
            context_new.EvaluationStack.Push(method);
            return true;
        }

        private static bool Contract_Destroy(ApplicationEngine engine)
        {
            UInt160 hash = engine.CurrentScriptHash;
            ContractState contract = engine.Snapshot.Contracts.TryGet(hash);
            if (contract == null) return true;
            engine.Snapshot.Contracts.Delete(hash);
            if (contract.HasStorage)
                foreach (var pair in engine.Snapshot.Storages.Find(hash.ToArray()))
                    engine.Snapshot.Storages.Delete(pair.Key);
            return true;
        }

        private static bool PutEx(ApplicationEngine engine, StorageContext context, byte[] key, byte[] value, StorageFlags flags)
        {
            if (key.Length > MaxStorageKeySize) return false;
            if (value.Length > MaxStorageValueSize) return false;
            if (context.IsReadOnly) return false;
            if (!CheckStorageContext(engine, context)) return false;

            StorageKey skey = new StorageKey
            {
                ScriptHash = context.ScriptHash,
                Key = key
            };

            if (engine.Snapshot.Storages.TryGet(skey)?.IsConstant == true) return false;

            if (value.Length == 0 && !flags.HasFlag(StorageFlags.Constant))
            {
                // If put 'value' is empty (and non-const), we remove it (implicit `Storage.Delete`)
                engine.Snapshot.Storages.Delete(skey);
            }
            else
            {
                StorageItem item = engine.Snapshot.Storages.GetAndChange(skey, () => new StorageItem());
                item.Value = value;
                item.IsConstant = flags.HasFlag(StorageFlags.Constant);
            }
            return true;
        }

        private static bool Storage_Put(ApplicationEngine engine)
        {
            if (!(engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface))
                return false;
            StorageContext context = _interface.GetInterface<StorageContext>();
            byte[] key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            byte[] value = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            return PutEx(engine, context, key, value, StorageFlags.None);
        }

        private static bool Storage_PutEx(ApplicationEngine engine)
        {
            if (!(engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface))
                return false;
            StorageContext context = _interface.GetInterface<StorageContext>();
            byte[] key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            byte[] value = engine.CurrentContext.EvaluationStack.Pop().GetByteArray();
            StorageFlags flags = (StorageFlags)(byte)engine.CurrentContext.EvaluationStack.Pop().GetBigInteger();
            return PutEx(engine, context, key, value, flags);
        }

        private static bool Storage_Delete(ApplicationEngine engine)
        {
            if (engine.CurrentContext.EvaluationStack.Pop() is InteropInterface _interface)
            {
                StorageContext context = _interface.GetInterface<StorageContext>();
                if (context.IsReadOnly) return false;
                if (!CheckStorageContext(engine, context)) return false;
                StorageKey key = new StorageKey
                {
                    ScriptHash = context.ScriptHash,
                    Key = engine.CurrentContext.EvaluationStack.Pop().GetByteArray()
                };
                if (engine.Snapshot.Storages.TryGet(key)?.IsConstant == true) return false;
                engine.Snapshot.Storages.Delete(key);
                return true;
            }
            return false;
        }
    }
}
