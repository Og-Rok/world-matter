using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public partial class JobManager : Node
{
	public static JobManager instance;
	private ConcurrentQueue<Job> job_queue = new ConcurrentQueue<Job>();
	private ConcurrentQueue<Job> completed_jobs = new ConcurrentQueue<Job>();
	private ConcurrentBag<JobGroup> job_groups = new ConcurrentBag<JobGroup>();
	private List<Thread> thread_pool = new List<Thread>();
	private Thread main_thread;

	public void _Ready()
	{
		instance = this;
		main_thread = Thread.CurrentThread;
	}

	public JobGroup makeJobGroup()
	{
		JobGroup group = new JobGroup(job_queue);
		job_groups.Add(group);
		return group;
	}

	public override void _Process(double delta)
	{
		if (thread_pool.Count < System.Environment.ProcessorCount && job_queue.Count > 0)
		{
			Thread thread = new Thread(new ThreadStart(workerThread));
			thread.Start();
			thread_pool.Add(thread);
		}

		if (completed_jobs.Count > 0 && completed_jobs.TryDequeue(out Job job))
		{
			try
			{
				if (job.errored && job.failure != null)
				{
					job.failure(job.result);
				}
				else if (!job.errored && job.success != null)
				{
					job.success(job.result);
				}
			} catch (Exception ex) {
				GD.PushError(ex);
			}
		}
	}

	private void workerThread()
	{
		while (main_thread != null && main_thread.IsAlive)
		{
			if (job_queue.TryDequeue(out Job job))
			{
				try
				{
					if (job.action == null)
					{
						throw new Exception("Job action is null");
					}

					job.result = job.action();
					job.errored = false;
				}
				catch (Exception ex)
				{
					job.result = ex;
					job.errored = true;
				}
				completed_jobs.Enqueue(job);
			}
			else
			{
				Thread.Sleep(100);
				if (!job_queue.TryPeek(out Job _))
				{
					break;
				}
			}
		}
	}

	public class JobGroup
	{
		private ConcurrentQueue<Job> manager_job_queue = new ConcurrentQueue<Job>();
		private ConcurrentDictionary<Job, Job> job_queue = new ConcurrentDictionary<Job, Job>();

		public JobGroup(ConcurrentQueue<Job> manager_job_queue)
		{
			this.manager_job_queue = manager_job_queue;
		}

		public void startJob(Func<object> action, Action<object> success = null, Action<object> failure = null)
		{
			Job job = new Job(action, success, failure);
			job_queue.TryAdd(job, job);
			manager_job_queue.Enqueue(job);
		}

		public void removeJob(Job job)
		{
			job_queue.TryRemove(job, out Job _);
		}

	}

	public class Job
	{
		public Func<object> action;
		public Action<object> success;
		public Action<object> failure;
		public Object result;
		public bool errored = false;

		public Job(Func<object> action, Action<object> success = null, Action<object> failure = null)
		{
			this.action = action;
			this.success = success;
			this.failure = failure;
		}
	}

	public class TimedJob : Job
	{
		public double timeout;
		public bool repeat = false;
		public TimedJob(Func<object> action, double timeout, bool repeat = false) : base(action, null, null)
		{
			this.timeout = timeout;
			this.repeat = repeat;
		}
	}
}