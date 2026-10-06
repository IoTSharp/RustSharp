use SourceProducer::fail;

struct Guard { value: i32 }
impl Drop for Guard {
    fn drop(&mut self) { println!("drop"); }
}

fn main() {
    let guard = Guard { value: 1 };
    println!("{}", fail(2147483647));
    println!("{}", guard.value);
}
