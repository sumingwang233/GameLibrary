using Xunit;

// 集成测试涉及真实文件系统、命名管道、子进程与 SQLite——并行运行会引入
// 文件锁/端口/管道竞争等环境噪声。禁用并行，全部串行执行保证确定性。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
