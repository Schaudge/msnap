// FillDisk.cpp : Defines the entry point for the console application.
//

#include "stdafx.h"
#include <Windows.h>

void usage() 
{
	fprintf(stderr, "usage: FillDisk pathmame");

	exit(1);
}

int main(int argc, char **argv)
{
	if (argc != 2) usage();

	HANDLE hFile = CreateFile(argv[1], GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);

	if (INVALID_HANDLE_VALUE == hFile) 
	{
		fprintf(stderr, "Unable to open file %s, %d\n", argv[1], GetLastError());
		return -1;
	}

	DWORD writeSize = 128 * 1024 * 1024;

	int *dataBuffer = (int *)VirtualAlloc(0, writeSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
	if (NULL == dataBuffer)
	{
		fprintf(stderr, "Unable to allocate buffer, %d\n", GetLastError());
		return -1;
	}

	for (int i = 0; i < writeSize / sizeof(*dataBuffer); i++)
	{
		dataBuffer[i] = i;
	}

	_int64 totalWritten = 0;
	_int64 oneDot = (_int64)100 * 1024 * 1024 * 1024;

	printf("Filling disk, one dot/100GB: ");

	while (writeSize > 8192) 
	{
		DWORD sizeWritten;

		if (!WriteFile(hFile, dataBuffer, writeSize, &sizeWritten, NULL)) 
		{
			if (GetLastError() == ERROR_DISK_FULL) 
			{
				writeSize /= 2;
			}
			else 
			{
				fprintf(stderr, "\nError writing: %d\n", GetLastError());
				exit(1);
			}
		}
		else {
			if (totalWritten / oneDot != (totalWritten + sizeWritten) / oneDot) {
				printf(".");
			}
			totalWritten += sizeWritten;
		}
	}

	printf("\n");


    return 0;
}

