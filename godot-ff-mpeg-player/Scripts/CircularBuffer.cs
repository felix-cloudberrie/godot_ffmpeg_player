using Godot;
using System;

public class CircularBuffer<T>
{
	private T[] _buffer;
	private int _head;
	private int _tail;

	public int Size { get; private set; }

	public CircularBuffer(int bufferLength)
	{
		_buffer = new T[bufferLength];
		_head = 0;
		_tail = 0;
	}

	public bool IsEmpty()
	{
		return Size == 0;
	}

	public bool IsFull()
	{
		return Size == _buffer.Length;
	}

	public void Push(T bufferItem)
	{
		if (IsFull())
		{
			GD.PushError("Circular Buffer is full!");
			return;
		}

		_buffer[_tail] = bufferItem;
		_tail = (_tail + 1) % _buffer.Length;
		++Size;
	}

	public T Pop()
	{
		if (IsEmpty())
		{
			GD.PushError("Circular Buffer is empty!");
			return default(T);
		}

		T bufferItem = _buffer[_head];
		_head = (_head + 1) % _buffer.Length;
		--Size;

		return bufferItem;
	}

	public T Peek()
	{
		return _buffer[_head];
	}
}