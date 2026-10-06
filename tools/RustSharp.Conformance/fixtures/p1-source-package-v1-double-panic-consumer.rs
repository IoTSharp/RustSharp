use SourceProducer::fail;

struct Guard { value: i32 }
impl Drop for Guard {
    fn drop(&mut self) {
        let value = 2147483647;
        println!("{}", value + 1);
    }
}

fn main() {
    let guard = Guard { value: 1 };
    println!("start");
    println!("{}", fail(2147483647));
    println!("{}", guard.value);
}
