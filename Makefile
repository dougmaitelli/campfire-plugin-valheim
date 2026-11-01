DOTNET ?= dotnet

.PHONY: restore build test format format-check lint check package

restore:
	$(DOTNET) restore Campfire.Valheim.slnx --locked-mode -p:Configuration=Release
	$(DOTNET) restore Campfire.Valheim.slnx --locked-mode -p:Configuration=Test

build: restore
	$(DOTNET) build Campfire.Valheim.slnx --no-restore --configuration Release

test: build
	$(DOTNET) test src/Campfire.Valheim/Campfire.Valheim.csproj --no-restore --configuration Test

format:
	Configuration=Release $(DOTNET) format Campfire.Valheim.slnx --no-restore --exclude src/Campfire.Valheim/obj
	Configuration=Test $(DOTNET) format Campfire.Valheim.slnx --no-restore --exclude src/Campfire.Valheim/obj

format-check:
	Configuration=Release $(DOTNET) format Campfire.Valheim.slnx --no-restore --verify-no-changes --exclude src/Campfire.Valheim/obj
	Configuration=Test $(DOTNET) format Campfire.Valheim.slnx --no-restore --verify-no-changes --exclude src/Campfire.Valheim/obj

lint: build
	Configuration=Release $(DOTNET) format analyzers Campfire.Valheim.slnx --no-restore --verify-no-changes --severity warn --exclude src/Campfire.Valheim/obj
	Configuration=Test $(DOTNET) format analyzers Campfire.Valheim.slnx --no-restore --verify-no-changes --severity warn --exclude src/Campfire.Valheim/obj

check: format-check lint test

package: build
	$(DOTNET) msbuild src/Campfire.Valheim/Campfire.Valheim.csproj -target:Package -property:Configuration=Release -verbosity:minimal
