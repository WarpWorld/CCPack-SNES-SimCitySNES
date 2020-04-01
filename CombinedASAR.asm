!UFOTrigger   = $7FF000
!init   = $7FF001

org $0080B6
	JSL nmi

org $0FE100
nmi:
	
	;LDA $1FF6
	;CMP #$20
	;BNE +

	LDA $14
	CMP #$00
	BNE +

	LDA $000002
	BNE +

	LDA $5F
	CMP #$01
	BNE +

	LDA !init
	CMP #$AB
	BEQ +

	LDA #$AB
	STA !init

	PHY
	PHX

	SEP #$10
	REP #$20

	LDA.w #GFX	;graphics location
	;CLC
	;ADC.l #$0800
	STA $4342
	LDY.b #GFX>>16
	STY $4344

	LDA #$AA00 ;address
	STA $2116


	REP #$10
	LDA #$1000	;4KB ExGFX file - load half
	STA $4345

	SEP #$20


	LDA #$80   ;DMA init
	STA $2115
	LDA #$18
	STA $4341
	LDA #$01
	STA $4340

	LDA #$10	;transfer
	STA $420B

	PLX
	PLY

	+
	LDA.L $0000B1
	RTL

org $03B9AE
	JML main

org $0FE000
main:
	SEP #$20
	LDA !UFOTrigger
	CMP #$AB
	REP #$20
	BNE +	

	SEP #$20
	LDA #$00
	STA !UFOTrigger
	REP #$20
	

	JML $03B9C1

	+
	CPY #$0006
	BNE +

	JML $03B9B3

	+
	JML $03B9C4

org $0FE200
GFX:
	incbin "GFX.bin"