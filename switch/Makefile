#---------------------------------------------------------------------------------
# Clear the implicit built in rules
#---------------------------------------------------------------------------------
.SUFFIXES:
#---------------------------------------------------------------------------------

ifeq ($(strip $(DEVKITPRO)),)
$(error "Please set DEVKITPRO in your environment. export DEVKITPRO=<path to>/devkitpro")
endif

include $(DEVKITPRO)/libnx/switch_rules

#---------------------------------------------------------------------------------
# TARGET is the name of the output
# SOURCES is a list of directories containing source code
# INCLUDES is a list of directories containing extra header files
#---------------------------------------------------------------------------------
TARGET		:=	DeskStream
SOURCES		:=	src
INCLUDES	:=	src

#---------------------------------------------------------------------------------
# options for code generation
#---------------------------------------------------------------------------------
ARCH_FLAGS	:=	-march=armv8-a+crc+crypto -mtune=cortex-a57 -mtp=el0 -fPIE

CFLAGS	:=	-g -Wall -O3 -ffunction-sections \
			$(ARCH_FLAGS) $(DEFINES)

CFLAGS	+=	`sdl2-config --cflags`

CXXFLAGS	:= $(CFLAGS) -fno-rtti -fno-exceptions -std=gnu++17

ASFLAGS	:=	-g $(ARCH_FLAGS)

LDFLAGS	=	-specs=$(DEVKITPRO)/libnx/switch.specs -g $(ARCH_FLAGS) -Wl,-Map,$(notdir $*.map)

LIBS	:=	-lavcodec -lavformat -lavutil -lswresample -lswscale -lz `sdl2-config --libs` -lnx

#---------------------------------------------------------------------------------
# list of templates and rules
#---------------------------------------------------------------------------------
ifneq ($(BUILD),$(notdir $(CURDIR)))

export OUTPUT	:=	$(CURDIR)/$(TARGET)
export TOPDIR	:=	$(CURDIR)

export VPATH	:=	$(foreach dir,$(SOURCES),$(CURDIR)/$(dir)) \
			$(foreach dir,$(DATA),$(CURDIR)/$(dir))

export DEPSDIR	:=	$(CURDIR)/build

CFILES		:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.c)))
CPPFILES	:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.cpp)))
SFILES		:=	$(foreach dir,$(SOURCES),$(notdir $(wildcard $(dir)/*.s)))

#---------------------------------------------------------------------------------
# use GFX_ROMFS for RomFS resources if needed
#---------------------------------------------------------------------------------
export OFILES_BIN	:=	
export OFILES_SRC	:=	$(CPPFILES:.cpp=.o) $(CFILES:.c=.o) $(SFILES:.s=.o)

export OFILES 	:=	$(OFILES_BIN) $(OFILES_SRC)

export HFILES	:=	$(foreach dir,$(INCLUDES),$(wildcard $(dir)/*.h)) \
			$(foreach dir,$(INCLUDES),$(wildcard $(dir)/*.hpp))

export INCLUDE	:=	$(foreach dir,$(INCLUDES),-I$(CURDIR)/$(dir)) \
			$(foreach dir,$(LIBDIRS),-I$(dir)/include) \
			-I$(DEVKITPRO)/libnx/include

.PHONY: clean all

#---------------------------------------------------------------------------------
all: $(DEPSDIR)
	@$(MAKE) --no-print-directory -C $(DEPSDIR) -f $(CURDIR)/Makefile BUILD=$(notdir $(CURDIR))

$(DEPSDIR):
	@mkdir -p $@

#---------------------------------------------------------------------------------
clean:
	@echo clean ...
	@rm -fr $(DEPSDIR) $(TARGET).elf $(TARGET).nro $(TARGET).nacp $(TARGET).map

#---------------------------------------------------------------------------------
else

DEPENDS	:=	$(OFILES:.o=.d)

#---------------------------------------------------------------------------------
# main targets
#---------------------------------------------------------------------------------
$(OUTPUT).nro:	$(OUTPUT).elf

$(OUTPUT).elf:	$(OFILES)

%.o:	%.cpp
	@echo $(notdir $<)
	@$(CXX) -MMD -MP -MF $(DEPSDIR)/$*.d $(CXXFLAGS) $(INCLUDE) -c $< -o $@ $(ERROR_FILTER)

%.o:	%.c
	@echo $(notdir $<)
	@$(CC) -MMD -MP -MF $(DEPSDIR)/$*.d $(CFLAGS) $(INCLUDE) -c $< -o $@ $(ERROR_FILTER)

%.o:	%.s
	@echo $(notdir $<)
	@$(AS) $(ASFLAGS) $< -o $@ $(ERROR_FILTER)

-include $(DEPENDS)

#---------------------------------------------------------------------------------
endif
#---------------------------------------------------------------------------------
