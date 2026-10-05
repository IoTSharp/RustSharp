// P1 Drop differential: a destructor fault during unwind aborts cleanup.
struct Failing;
fn overflow(value: i32) { println!("{}", value + 1); }
impl Drop for Failing {
    fn drop(&mut self) {
        println!("drop-start");
        overflow(2147483647);
    }
}
fn main() {
    let _owner = Failing;
    println!("body");
    overflow(2147483647);
}
