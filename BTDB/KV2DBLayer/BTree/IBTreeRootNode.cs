namespace BTDB.KV2DBLayer.BTree
{
    internal interface IBTreeRootNode : IBTreeNode
    {
        long TransactionId { get; }
        IBTreeRootNode NewTransactionRoot();
        void EraseRange(long firstKeyIndex, long lastKeyIndex);
    }
}